using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

// SF Body Morphs avatar bundles (docs/SOURCES.md, 'A third mode: avatar replacement'), Unity 2022.3.9f1 like the game.
// tools/avatar/build.ps1 copies a character's FBX, its textures and DAZ's materials.txt into Assets/Avatars/<Name>, then:
//   Unity.exe -batchmode -quit -nographics -projectPath <this project> -executeMethod AvatarBundle.Build -name <Name> -out <dir>
//   Unity.exe ... -executeMethod AvatarBundle.Verify -bundle <file>
// The bundle holds the character's prefab (readable meshes, its own skeleton with DAZ's follower figures rebound to it),
// its textures and avatar.txt (surfaces, roles, texture per channel). No shaders: the plugin builds the materials at load
// from the game's own shaders, so the HDRP variants are the game's.
public static class AvatarBundle
{
    const string Root = "Assets/Avatars";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static string Arg(string name, string def = null)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
        return def;
    }

    static void Log(string s) { Debug.Log("SfAvatar: " + s); }

    static void Fail(string s)
    {
        Debug.LogError("SfAvatar FAILED: " + s);
        EditorApplication.Exit(1);
    }

    // ---- texture kinds -------------------------------------------------------------------------------------------
    // albedo / normal / detailnormal / roughness / translucency / opacity (+ generated smooth, mask, albedoalpha)
    static string KindOfDaz(string channel)
    {
        var c = channel.ToLowerInvariant();
        if (c == "base color" || c == "diffuse color" || c == "color") return "albedo";
        if (c == "normal map" || c == "normal") return "normal";
        if (c.StartsWith("detail normal")) return "detailnormal";
        if (c.Contains("roughness")) return "roughness";
        if (c.StartsWith("translucency color") || c.StartsWith("sss color")) return "translucency";
        if (c.Contains("cutout opacity") || c == "opacity") return "opacity";
        return null;
    }

    static string KindOfFbx(string prop)
    {
        var p = prop.ToLowerInvariant();
        if (p == "diffusecolor" || p == "basecolor" || p == "maintex") return "albedo";
        if (p == "normalmap" || p == "bump") return "normal";
        if (p.StartsWith("transparen") || p == "opacity") return "opacity";
        if (p.Contains("roughness")) return "roughness";
        return null;
    }

    static readonly string[] KindOrder = { "normal", "albedo", "detailnormal", "translucency", "opacity", "roughness" };

    // surface role from its material name (whole words, lower case)
    static string RoleOf(string surface)
    {
        var s = " " + Regex.Replace(surface.ToLowerInvariant(), @"[_\-.]+", " ") + " ";
        if (Regex.IsMatch(s, @"moisture|cornea")) return "cornea";
        if (Regex.IsMatch(s, @"\btear")) return "tear";
        if (Regex.IsMatch(s, @"lash")) return "lashes";
        if (Regex.IsMatch(s, @"brow")) return "brows";
        if (Regex.IsMatch(s, @"\beye ?(left|right|ball|s)?\b|\biris|\bsclera|\bpupil")) return "eye";
        if (Regex.IsMatch(s, @"teeth|tooth")) return "teeth";
        if (Regex.IsMatch(s, @"^ mouth( \d+)? $|tongue|gums")) return "mouth";
        if (Regex.IsMatch(s, @"nail")) return "nails";
        return "skin";
    }

    // ---- the FBX's own material textures, recorded during its import --------------------------------------------------
    internal static readonly Dictionary<string, Dictionary<string, string>> FbxTex = new Dictionary<string, Dictionary<string, string>>();
    // and its constant colour / opacity (lashes, brows, tear): "color=r,g,b"
    internal static readonly Dictionary<string, string> FbxConst = new Dictionary<string, string>();

    // ---- build -----------------------------------------------------------------------------------------------------------
    public static void Build()
    {
        try { BuildInner(); }
        catch (Exception e) { Fail(e.ToString()); }
    }

    static void BuildInner()
    {
        string name = Arg("-name"), outDir = Arg("-out", "Build");
        int maxSize = int.Parse(Arg("-maxsize", "4096"), Inv), mapSize = int.Parse(Arg("-mapsize", "2048"), Inv);
        if (string.IsNullOrEmpty(name)) { Fail("-name missing"); return; }
        string dir = Root + "/" + name;
        if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.Refresh();
        if (!AssetDatabase.IsValidFolder(dir)) { Fail(dir + " not found (build.ps1 copies the character there)"); return; }
        string fbx = Directory.GetFiles(dir, "*.fbx").Select(p => p.Replace('\\', '/')).FirstOrDefault();
        if (fbx == null) { Fail("no .fbx in " + dir); return; }

        FbxTex.Clear(); FbxConst.Clear();
        AssetDatabase.ImportAsset(fbx, ImportAssetOptions.ForceUpdate);
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
        if (model == null) { Fail("the FBX did not import: " + fbx); return; }

        // texture assets in the folder by file name
        var texByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in AssetDatabase.FindAssets("t:Texture2D", new[] { dir }))
        {
            var p = AssetDatabase.GUIDToAssetPath(g);
            if (!p.Contains("/Generated/")) texByFile[Path.GetFileName(p)] = p;
        }

        // surfaces: every material on the model's renderers; channels from DAZ's materials.txt, else the FBX's material
        var daz = ReadDaz(dir + "/materials.txt");
        var surfaces = new List<string>();
        foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            foreach (var m in r.sharedMaterials)
                if (m != null && !surfaces.Contains(m.name)) surfaces.Add(m.name);
        var chan = new Dictionary<string, Dictionary<string, string>>();   // surface -> kind -> texture asset path
        foreach (var s in surfaces)
        {
            var k = new Dictionary<string, string>();
            if (daz.TryGetValue(s, out var dl))
                foreach (var kv in dl)
                    if (texByFile.TryGetValue(kv.Value, out var tp) && !k.ContainsKey(kv.Key)) k[kv.Key] = tp;
            if (FbxTex.TryGetValue(s, out var fl))
                foreach (var kv in fl)
                {
                    var kind = KindOfFbx(kv.Key);
                    if (kind != null && !k.ContainsKey(kind) && texByFile.TryGetValue(kv.Value, out var tp)) k[kind] = tp;
                }
            chan[s] = k;
        }

        // importer settings per texture: its most important use decides
        var kindOf = new Dictionary<string, string>();
        foreach (var k in chan.Values)
            foreach (var kv in k)
                if (!kindOf.TryGetValue(kv.Value, out var old) || Array.IndexOf(KindOrder, kv.Key) < Array.IndexOf(KindOrder, old))
                    kindOf[kv.Value] = kv.Key;
        foreach (var kv in kindOf) SetupTexture(kv.Key, kv.Value, maxSize, mapSize, readable: kv.Value == "roughness" || kv.Value == "opacity");
        // albedo textures that get an opacity merged in must be readable too
        foreach (var k in chan.Values)
            if (k.ContainsKey("opacity") && k.ContainsKey("albedo")) SetupTexture(k["albedo"], "albedo", maxSize, mapSize, readable: true);

        // generated maps: smoothness (1 - roughness, R) for the game's Skin shader, an HDRP mask map (A = smoothness),
        // albedo with the opacity in alpha for cut-out / transparent surfaces
        string gen = dir + "/Generated";
        if (!AssetDatabase.IsValidFolder(gen)) AssetDatabase.CreateFolder(dir, "Generated");
        var genDone = new Dictionary<string, string>();
        foreach (var s in surfaces)
        {
            var k = chan[s];
            if (k.TryGetValue("roughness", out var rp))
            {
                k["smooth"] = Generated(genDone, gen, rp, "_smooth", src => Map(src, c => { float v = 1f - c.r; return new Color(v, v, v, 1f); }), "smooth", mapSize);
                k["mask"] = Generated(genDone, gen, rp, "_mask", src => Map(src, c => new Color(0f, 1f, 0f, 1f - c.r)), "mask", mapSize);
            }
            if (k.TryGetValue("opacity", out var op) && k.TryGetValue("albedo", out var ap))
            {
                var key = ap + "|" + op;
                if (!genDone.TryGetValue(key, out var gp))
                {
                    var a = AssetDatabase.LoadAssetAtPath<Texture2D>(ap); var o = AssetDatabase.LoadAssetAtPath<Texture2D>(op);
                    int w = a.width, h = a.height;
                    var px = a.GetPixels();
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            px[y * w + x].a = o.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h).r;
                    gp = WritePng(gen + "/" + Path.GetFileNameWithoutExtension(ap) + "_" + Path.GetFileNameWithoutExtension(op) + ".png", w, h, px);
                    SetupTexture(gp, "albedoalpha", maxSize, mapSize, readable: false);
                    genDone[key] = gp;
                }
                k["albedoalpha"] = gp;
            }
        }
        // the bundle carries no roughness / opacity originals (their generated maps replace them) unless nothing replaced them
        foreach (var k in chan.Values)
        {
            if (k.ContainsKey("smooth")) k.Remove("roughness");
            if (k.ContainsKey("albedoalpha")) { k.Remove("opacity"); k.Remove("albedo"); }
        }
        // readable copies were only needed for the generated maps
        foreach (var kv in kindOf) if (kv.Value == "roughness" || kv.Value == "opacity") SetupTexture(kv.Key, kv.Value, maxSize, mapSize, readable: false);
        foreach (var k in chan.Values) if (k.ContainsKey("albedoalpha") && k.ContainsKey("albedo")) SetupTexture(k["albedo"], "albedo", maxSize, mapSize, readable: false);

        // the prefab
        var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
        PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        go.name = "Avatar";
        var notes = new List<string>();
        foreach (var an in go.GetComponentsInChildren<Animator>(true)) UnityEngine.Object.DestroyImmediate(an);   // the plugin drives the bones
        var smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (smrs.Length == 0) { Fail("no skinned meshes in " + fbx); return; }
        var main = smrs.OrderByDescending(r => r.sharedMesh != null ? r.sharedMesh.vertexCount : 0).First();
        Transform top = Common(main.bones.Where(b => b != null).ToList());
        while (top.parent != null && top.parent != go.transform && top.parent.GetComponent<Renderer>() == null && top.parent.childCount == 1) top = top.parent;
        var mainSet = new HashSet<Transform>(top.GetComponentsInChildren<Transform>(true));
        var byName = new Dictionary<string, Transform>();
        foreach (var t in top.GetComponentsInChildren<Transform>(true)) if (!byName.ContainsKey(t.name)) byName[t.name] = t;
        var rebound = new Dictionary<SkinnedMeshRenderer, string>();
        foreach (var r in smrs)
        {
            if (r == main) { rebound[r] = "main"; continue; }
            var bones = r.bones;
            var nb = new Transform[bones.Length];
            int moved = 0, grafted = 0, bad = 0;
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones[i];
                if (b == null || mainSet.Contains(b)) { nb[i] = b; continue; }
                if (byName.TryGetValue(b.name, out var m))
                {
                    if (Same(b, m)) { nb[i] = m; moved++; }
                    else { nb[i] = b; bad++; }
                    continue;
                }
                // a bone only the follower has (the G9 mouth's tongue): graft it onto the main skeleton under its nearest
                // ancestor the main skeleton has, keeping its rest pose
                var a = b.parent;
                while (a != null && !byName.ContainsKey(a.name)) a = a.parent;
                if (a == null || !Same(a, byName[a.name])) { nb[i] = b; bad++; continue; }
                b.SetParent(byName[a.name], true);
                foreach (var t in b.GetComponentsInChildren<Transform>(true)) { mainSet.Add(t); if (!byName.ContainsKey(t.name)) byName[t.name] = t; }
                nb[i] = b; grafted++;
            }
            if (bad == 0)
            {
                r.bones = nb;
                if (r.rootBone != null && !mainSet.Contains(r.rootBone) && byName.TryGetValue(r.rootBone.name, out var rb)) r.rootBone = rb;
                rebound[r] = "rebound " + moved + (grafted > 0 ? " + grafted " + grafted : "");
            }
            else rebound[r] = "own skeleton (" + bad + " bones differ from the main one)";
            Log("renderer " + r.name + ": " + rebound[r]);
        }
        // stray static meshes (DAZ's FBX carries a 'Cube') and skeletons nothing uses any more
        foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true).ToArray())
        {
            notes.Add("removed static mesh " + mr.name);
            UnityEngine.Object.DestroyImmediate(mr.gameObject);
        }
        smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var used = new HashSet<Transform>(mainSet);
        void Up(Transform t) { while (t != null && used.Add(t)) t = t.parent; }
        foreach (var r in smrs)
        {
            Up(r.transform); Up(r.rootBone);
            foreach (var b in r.bones) Up(b);
        }
        foreach (var t in mainSet.ToArray()) Up(t);
        var drop = go.GetComponentsInChildren<Transform>(true).Where(t => !used.Contains(t) && (t.parent == null || used.Contains(t.parent))).ToList();
        foreach (var t in drop) { notes.Add("removed unused " + t.name + " (" + t.GetComponentsInChildren<Transform>(true).Length + " transforms)"); UnityEngine.Object.DestroyImmediate(t.gameObject); }

        // height from the main mesh at rest
        var baked = new Mesh();
        main.BakeMesh(baked, true);
        var l2w = main.transform.localToWorldMatrix;
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var v in baked.vertices) { float y = l2w.MultiplyPoint3x4(v).y; lo = Mathf.Min(lo, y); hi = Mathf.Max(hi, y); }
        UnityEngine.Object.DestroyImmediate(baked);

        string prefabPath = dir + "/" + name + ".prefab";
        PrefabUtility.SaveAsPrefabAsset(go, prefabPath);

        // avatar.txt
        var sb = new StringBuilder();
        sb.Append("# SfMorph avatar v1 (tools/avatar/build.ps1)\n");
        sb.Append("name ").Append(name).Append('\n');
        sb.Append("source ").Append(Path.GetFileName(fbx)).Append('\n');
        sb.Append("built ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv)).Append('\n');
        sb.Append("unity ").Append(Application.unityVersion).Append('\n');
        sb.Append("prefab ").Append(name).Append('\n');
        sb.Append("height ").Append((hi - lo).ToString("0.###", Inv)).Append(' ').Append(lo.ToString("0.###", Inv)).Append('\n');
        sb.Append("skeleton ").Append(top.name).Append(' ').Append(mainSet.Count).Append('\n');
        sb.Append("# renderer <name>\\t<mesh>\\t<vertices>\\t<bones>\\t<blend shapes>\\t<materials, ';'>\\t<skeleton>\n");
        foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var m = r.sharedMesh;
            sb.Append("renderer ").Append(r.name).Append('\t').Append(m != null ? m.name : "-").Append('\t').Append(m != null ? m.vertexCount : 0)
              .Append('\t').Append(r.bones.Length).Append('\t').Append(m != null ? m.blendShapeCount : 0).Append('\t')
              .Append(string.Join(";", r.sharedMaterials.Select(x => x != null ? x.name : "-"))).Append('\t')
              .Append(rebound.TryGetValue(r, out var rn) ? rn : "?").Append('\n');
        }
        sb.Append("# surface <material>\\t<role>\\t<kind>=<texture>,... (kinds: albedo, albedoalpha, normal, detailnormal, smooth, mask, translucency, opacity, roughness)\\t<constants: color=r,g,b>\n");
        var bundleTex = new HashSet<string>();
        foreach (var s in surfaces)
        {
            var k = chan[s];
            sb.Append("surface ").Append(s).Append('\t').Append(RoleOf(s)).Append('\t')
              .Append(string.Join(",", k.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + Path.GetFileNameWithoutExtension(kv.Value)))).Append('\t')
              .Append(FbxConst.TryGetValue(s, out var cst) ? cst : "").Append('\n');
            foreach (var p in k.Values) bundleTex.Add(p);
        }
        foreach (var n in notes) sb.Append("# ").Append(n).Append('\n');
        string manifestPath = dir + "/avatar.txt";
        File.WriteAllText(manifestPath, sb.ToString());
        AssetDatabase.ImportAsset(manifestPath, ImportAssetOptions.ForceUpdate);
        UnityEngine.Object.DestroyImmediate(go);

        // the bundle
        string bundle = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        Directory.CreateDirectory(outDir);
        var assets = new List<string> { prefabPath, manifestPath };
        assets.AddRange(bundleTex);
        var build = new AssetBundleBuild { assetBundleName = bundle, assetNames = assets.ToArray() };
        var manifest = BuildPipeline.BuildAssetBundles(outDir, new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, BuildTarget.StandaloneWindows64);
        if (manifest == null) { Fail("BuildAssetBundles failed"); return; }
        var file = Path.Combine(outDir, bundle);
        File.Copy(manifestPath, Path.Combine(outDir, "avatar.txt"), true);
        Log("built " + Path.GetFullPath(file) + " (" + (new FileInfo(file).Length / 1048576.0).ToString("0.0", Inv) + " MB): " +
            smrs.Length + " skinned meshes, " + surfaces.Count + " surfaces, " + bundleTex.Count + " textures, height " +
            (hi - lo).ToString("0.000", Inv) + " m");
        foreach (var n in notes) Log(n);
    }

    static Dictionary<string, Dictionary<string, string>> ReadDaz(string path)
    {
        var d = new Dictionary<string, Dictionary<string, string>>();
        if (!File.Exists(path)) return d;
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith("#") || line.Trim().Length == 0) continue;
            var t = line.Split('\t');
            if (t.Length < 3) continue;
            var kind = KindOfDaz(t[1].Trim());
            if (kind == null) continue;
            if (!d.TryGetValue(t[0], out var k)) d[t[0]] = k = new Dictionary<string, string>();
            if (!k.ContainsKey(kind)) k[kind] = Path.GetFileName(t[2].Trim().Replace('\\', '/'));
        }
        return d;
    }

    static void SetupTexture(string path, string kind, int maxSize, int mapSize, bool readable)
    {
        var ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) return;
        bool big = kind == "albedo" || kind == "normal" || kind == "albedoalpha";
        bool linear = kind == "roughness" || kind == "opacity" || kind == "smooth" || kind == "mask" || kind == "normal" || kind == "detailnormal";
        ti.textureType = (kind == "normal" || kind == "detailnormal") ? TextureImporterType.NormalMap : TextureImporterType.Default;
        ti.sRGBTexture = !linear;
        ti.mipmapEnabled = true;
        ti.isReadable = readable;
        ti.alphaSource = kind == "albedoalpha" || kind == "mask" ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
        ti.alphaIsTransparency = false;
        ti.wrapMode = TextureWrapMode.Repeat;
        ti.maxTextureSize = big ? maxSize : mapSize;
        var fmt = readable ? TextureImporterFormat.RGBA32
            : kind == "normal" || kind == "detailnormal" ? TextureImporterFormat.BC5
            : kind == "smooth" || kind == "roughness" || kind == "opacity" ? TextureImporterFormat.BC4
            : TextureImporterFormat.BC7;
        ti.textureCompression = readable ? TextureImporterCompression.Uncompressed : TextureImporterCompression.CompressedHQ;
        ti.SetPlatformTextureSettings(new TextureImporterPlatformSettings
        {
            name = "Standalone", overridden = true, maxTextureSize = ti.maxTextureSize, format = fmt,
            textureCompression = ti.textureCompression, compressionQuality = 50
        });
        ti.SaveAndReimport();
    }

    static string Generated(Dictionary<string, string> done, string gen, string src, string suffix, Func<Texture2D, Color[]> f, string kind, int mapSize)
    {
        var key = src + suffix;
        if (done.TryGetValue(key, out var p)) return p;
        var t = AssetDatabase.LoadAssetAtPath<Texture2D>(src);
        p = WritePng(gen + "/" + Path.GetFileNameWithoutExtension(src) + suffix + ".png", t.width, t.height, f(t));
        SetupTexture(p, kind, mapSize, mapSize, readable: false);
        done[key] = p;
        return p;
    }

    static Color[] Map(Texture2D t, Func<Color, Color> f)
    {
        var px = t.GetPixels();
        for (int i = 0; i < px.Length; i++) px[i] = f(px[i]);
        return px;
    }

    static string WritePng(string path, int w, int h, Color[] px)
    {
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
        t.SetPixels(px);
        t.Apply(false);
        File.WriteAllBytes(path, t.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(t);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        return path;
    }

    static Transform Common(List<Transform> ts)
    {
        var chain = new List<Transform>();
        for (var t = ts[0]; t != null; t = t.parent) chain.Add(t);
        foreach (var t in ts)
        {
            var anc = new HashSet<Transform>();
            for (var a = t; a != null; a = a.parent) anc.Add(a);
            chain = chain.Where(anc.Contains).ToList();
        }
        return chain[0];
    }

    // same rest transform within 0.1 mm and ~0.06 degrees
    static bool Same(Transform a, Transform b)
    {
        if ((a.position - b.position).magnitude > 1e-4f) return false;
        return Quaternion.Angle(a.rotation, b.rotation) < 0.06f && (a.lossyScale - b.lossyScale).magnitude < 1e-3f;
    }

    // ---- verify: load the built bundle the way the plugin will ---------------------------------------------------------
    public static void Verify()
    {
        AssetBundle b = null;
        try
        {
            var path = Arg("-bundle");
            b = AssetBundle.LoadFromFile(path);
            if (b == null) { Fail("could not load " + path); return; }
            var txt = b.LoadAsset<TextAsset>("avatar");
            if (txt == null) { Fail("no avatar.txt in the bundle"); return; }
            var lines = txt.text.Split('\n');
            string prefab = lines.Where(l => l.StartsWith("prefab ")).Select(l => l.Substring(7).Trim()).FirstOrDefault();
            var p = b.LoadAsset<GameObject>(prefab);
            if (p == null) { Fail("prefab '" + prefab + "' not in the bundle (" + string.Join(", ", b.GetAllAssetNames()) + ")"); return; }
            var go = UnityEngine.Object.Instantiate(p);
            var smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var problems = new List<string>();
            Transform skel = null;
            foreach (var r in smrs)
            {
                var m = r.sharedMesh;
                if (m == null) { problems.Add(r.name + ": no mesh"); continue; }
                if (!m.isReadable) problems.Add(r.name + ": mesh not readable");
                if (r.bones.Any(x => x == null)) problems.Add(r.name + ": missing bones");
                if (m.bindposes.Length != r.bones.Length) problems.Add(r.name + ": " + m.bindposes.Length + " bind poses for " + r.bones.Length + " bones");
                var root = r.bones.Length > 0 ? r.bones[0] : null;
                while (root != null && root.parent != null && root.parent != go.transform) root = root.parent;
                if (skel == null) skel = root; else if (root != skel) problems.Add(r.name + ": on another skeleton (" + (root != null ? root.name : "-") + ")");
                // the rest pose drawn by the skin matches the mesh: bone x bind pose = renderer space at rest
                float err = 0f;
                for (int i = 0; i < r.bones.Length; i++)
                {
                    var mm = r.transform.worldToLocalMatrix * r.bones[i].localToWorldMatrix * m.bindposes[i];
                    for (int c = 0; c < 16; c++) err = Mathf.Max(err, Mathf.Abs(mm[c] - Matrix4x4.identity[c]));
                }
                Log("verify " + r.name + ": " + m.vertexCount + " vertices, " + r.bones.Length + " bones, " + m.blendShapeCount +
                    " blend shapes, readable " + m.isReadable + ", rest error " + err.ToString("0.00000", Inv) + ", materials " +
                    string.Join(";", r.sharedMaterials.Select(x => x != null ? x.name : "-")));
                if (err > 1e-3f) problems.Add(r.name + ": rest pose off by " + err.ToString("0.0000", Inv));
            }
            int tex = 0;
            foreach (var l in lines.Where(l => l.StartsWith("surface ")))
                foreach (var kv in (l.Split('\t').ElementAtOrDefault(2) ?? "").Split(',').Where(x => x.Contains("=")))
                {
                    var t = b.LoadAsset<Texture2D>(kv.Split('=')[1].Trim());
                    if (t == null) problems.Add("texture " + kv + " missing"); else tex++;
                }
            var names = b.GetAllAssetNames();
            Log("verify: " + smrs.Length + " skinned meshes on skeleton " + (skel != null ? skel.name : "-") + ", " + tex + " surface textures found, " +
                names.Length + " assets in the bundle");
            UnityEngine.Object.DestroyImmediate(go);
            if (problems.Count > 0) { Fail("verify: " + string.Join("; ", problems)); return; }
            Log("verify OK");
        }
        catch (Exception e) { Fail(e.ToString()); }
        finally { if (b != null) b.Unload(true); }
    }
}

// Import settings for everything under Assets/Avatars: readable meshes on their own skeleton (Generic rig, no avatar, no animation),
// blend shapes kept, units from the file; the FBX's own material textures recorded for avatar.txt.
public class AvatarImport : AssetPostprocessor
{
    void OnPreprocessModel()
    {
        if (!assetPath.StartsWith("Assets/Avatars/")) return;
        var mi = (ModelImporter)assetImporter;
        mi.animationType = ModelImporterAnimationType.Generic;   // None would import the meshes without their skin
        mi.avatarSetup = ModelImporterAvatarSetup.NoAvatar;
        mi.optimizeGameObjects = false;
        mi.importAnimation = false;
        mi.isReadable = true;
        mi.importBlendShapes = true;
        mi.importVisibility = false;
        mi.importCameras = false;
        mi.importLights = false;
        mi.useFileScale = true;
        mi.globalScale = 1f;
        mi.meshCompression = ModelImporterMeshCompression.Off;
        mi.importNormals = ModelImporterNormals.Import;
        mi.importTangents = ModelImporterTangents.CalculateMikk;
        mi.skinWeights = ModelImporterSkinWeights.Standard;   // 4 per vertex, as the game skins
        mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
        mi.materialLocation = ModelImporterMaterialLocation.InPrefab;
    }

    void OnPreprocessMaterialDescription(MaterialDescription d, Material m, AnimationClip[] clips)
    {
        if (!assetPath.StartsWith("Assets/Avatars/")) return;
        var names = new List<string>();
        d.GetTexturePropertyNames(names);
        var k = new Dictionary<string, string>();
        foreach (var n in names)
            if (d.TryGetProperty(n, out TexturePropertyDescription t))
            {
                // Unity resolves the FBX's texture references to assets; path / relativePath may hold that asset's GUID
                var f = t.texture != null ? AssetDatabase.GetAssetPath(t.texture) : null;
                foreach (var c in new[] { t.relativePath, t.path })
                    if (string.IsNullOrEmpty(f) && !string.IsNullOrEmpty(c))
                        f = Regex.IsMatch(c, "^[0-9a-f]{32}$") ? AssetDatabase.GUIDToAssetPath(c) : c;
                if (!string.IsNullOrEmpty(f)) k[n] = Path.GetFileName(f.Replace('\\', '/'));
            }
        AvatarBundle.FbxTex[d.materialName] = k;
        var cs = new List<string>();
        var inv = CultureInfo.InvariantCulture;
        if (d.TryGetProperty("DiffuseColor", out Vector4 col))
        {
            float f = d.TryGetProperty("DiffuseFactor", out float df) ? df : 1f;
            cs.Add("color=" + (col.x * f).ToString("0.###", inv) + "," + (col.y * f).ToString("0.###", inv) + "," + (col.z * f).ToString("0.###", inv));
        }
        // no opacity constant: DAZ's FBX gives TransparencyFactor 1 on every surface, skin included; the plugin goes by the
        // surface's role (cornea and tear see-through, lashes and brows cut out by their maps)
        AvatarBundle.FbxConst[d.materialName] = string.Join(" ", cs);
        Debug.Log("SfAvatar: fbx material " + d.materialName + ": " + (names.Count == 0 ? "no textures" :
            string.Join(", ", names.Select(n => n + "=" + (k.TryGetValue(n, out var f) ? f : "?")))));
    }
}
