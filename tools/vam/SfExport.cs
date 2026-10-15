// SF character export: a Virt-A-Mate plugin (add it to a Person atom) that writes the
// character as VaM shows it, for the SF Body Morphs character pipeline (docs/PIPELINE.md):
//   body.obj        Genesis 2 mesh with every morph applied, NOT posed (rest frame, metres,
//                   Unity axes), UV vertices with UVs, one group per material
//   joints.txt      every DAZ bone: name, parent, morphed rest position, posed position
//   materials.txt   material index, name, enabled, region, and the PNG written per texture slot
//   urls.txt        the texture files the appearance uses (face/torso/limbs/genitals)
//   tex_*.png       the textures VaM renders with (diffuse, specular, gloss, normal, decal)
//   thumb.jpg       a portrait
// The second button exports individual morph deltas (slider morphs + pJCM correctives):
//   morphs/<name>.delta  sparse per-UV-vertex delta (probe step) in body.obj's frame and scale
//   morphs.txt           one row per morph: value, probe, pose control, region, found/missing, |D|max mm, nonzero
//   info.txt             gets the base drift lines appended
// Morph list: Custom/Scripts/SfMod/morphs.cfg next to the plugin (built-in defaults if absent).
// into Saves\PluginData\SfExport\<atom>-<time>\ (VaM lets plugins write there without asking).
// Personal use: the output contains DAZ/VaM content; never share it.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using MVR.FileManagementSecure;

public class SfExport : MVRScript
{
    JSONStorableString _status;
    bool _busy;
    string _lastDir;
    const string CfgPath = "Custom/Scripts/SfMod/morphs.cfg";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public override void Init()
    {
        if (containingAtom == null || containingAtom.GetComponentInChildren<DAZCharacterSelector>() == null)
        {
            SuperController.LogError("SF export: add this plugin to a Person atom");
            return;
        }
        _status = new JSONStorableString("status", "Load the look (e.g. an appearance preset), then press Export.");
        var b = CreateButton("Export character for SF");
        b.button.onClick.AddListener(() => { if (!_busy) StartCoroutine(Run()); });
        var b2 = CreateButton("Export morphs for SF (sliders + correctives)");
        b2.button.onClick.AddListener(() => { if (!_busy) StartCoroutine(RunMorphs()); });
        CreateTextField(_status);
    }

    void Say(string s) { if (_status != null) _status.val = s; SuperController.LogMessage("SF export: " + s); }
    void Progress(string s) { if (_status != null) _status.val = s; }

    // The atom's auto blink flag (it drives the eyelid morphs), or null.
    JSONStorableBool FindBlink()
    {
        foreach (var id in containingAtom.GetStorableIDs())
        {
            var st = containingAtom.GetStorableByID(id);
            var b = st != null ? st.GetBoolJSONParam("blinkEnabled") : null;
            if (b != null) return b;
        }
        return null;
    }

    IEnumerator Run()
    {
        _busy = true;
        var selector = containingAtom.GetComponentInChildren<DAZCharacterSelector>();
        var character = selector != null ? selector.selectedCharacter : null;
        if (character == null) { Say("no character selected"); _busy = false; yield break; }
        DAZSkinV2 skin = character.GetComponentInChildren<DAZMergedSkinV2>();
        if (skin == null) skin = character.skin;
        var mesh = skin != null ? skin.dazMesh : null;
        if (mesh == null) { Say("character has no skin mesh yet"); _busy = false; yield break; }

        string dir = "Saves/PluginData/SfExport/" + Safe(containingAtom.uid) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv);
        FileManagerSecure.CreateDirectory(dir);
        _lastDir = dir;
        Say("exporting to " + dir + " ...");

        // Morphed, unposed vertices: ask VaM's character runner for a snapshot (what VaM's own OBJ
        // export does), so the threaded morph update can't hand us a half-updated array.
        Vector3[] verts = null, normals = null;
        // Auto blink drives the eyelid morphs: a snapshot mid-blink gives the character half-closed eyes for good.
        // Blink off and the lids open again for the snapshot, then back as it was.
        JSONStorableBool blink = FindBlink();
        bool blinkWas = blink != null && blink.val;
        if (blinkWas) { blink.val = false; Say("eyes open for the snapshot ..."); yield return new WaitForSeconds(0.8f); }
        var run = containingAtom.GetComponentInChildren<DAZCharacterRun>();
        if (run != null)
        {
            run.doSnap = true;
            for (int i = 0; i < 6; i++) yield return null;
            verts = run.snappedMorphedUVVertices;
            normals = run.snappedMorphedUVNormals;
        }
        if (blinkWas) blink.val = true;
        string source = "snapshot";
        if (verts == null || verts.Length != mesh.numUVVertices) { verts = mesh.morphedUVVertices; normals = mesh.morphedUVNormals; source = "dazMesh.morphedUVVertices"; }
        if (verts == null) { Say("no morphed vertices available"); _busy = false; yield break; }

        var matNames = mesh.materialNames;
        var enabled = mesh.materialsEnabled;
        FileManagerSecure.WriteAllText(dir + "/body.obj", Obj("VaM " + character.displayName + " morphed rest pose (" + source + ")", verts, normals, mesh.UV, mesh.UVPolyList, matNames));
        // No posed mesh: VaM skins on the GPU and its CPU copy (rawSkinnedVerts) is only kept up to date for
        // the vertices its physics uses, so a posed export comes out torn. The fit uses the rest pose + joints.
        int stretched = Stretched(verts, mesh.UVPolyList);
        yield return null;

        // Joints in the same rest frame as body.obj (morphedWorldPosition), plus where they are now.
        var sb = new StringBuilder("# name\tparent\tmorphed rest position\tposed position\n");
        var bones = skin.root != null ? skin.root.dazBones : null;
        if (bones != null)
            foreach (var bone in bones)
            {
                if (bone == null) continue;
                sb.Append(bone.name).Append('\t').Append(bone.parentBone != null ? bone.parentBone.name : "-").Append('\t')
                  .Append(V(bone.morphedWorldPosition)).Append('\t').Append(V(bone.transform.position)).Append('\n');
            }
        FileManagerSecure.WriteAllText(dir + "/joints.txt", sb.ToString());

        // Textures as rendered, one PNG per distinct texture.
        var tc = character.GetComponentInChildren<DAZCharacterTextureControl>();
        var written = new Dictionary<int, string>();
        var mats = new StringBuilder("# index\tname\tenabled\tregion\tslot=file ...\n");
        var gpu = skin.GPUmaterials;
        string[] slots = { "_MainTex", "_SpecTex", "_GlossTex", "_BumpMap", "_DecalTex" };
        for (int i = 0; gpu != null && i < gpu.Length; i++)
        {
            var m = gpu[i];
            mats.Append(i).Append('\t').Append(matNames != null && i < matNames.Length ? matNames[i] : "-").Append('\t')
                .Append(enabled != null && i < enabled.Length && enabled[i] ? "1" : "0").Append('\t').Append(Region(tc, i));
            if (m != null)
                foreach (var slot in slots)
                {
                    if (!m.HasProperty(slot)) continue;
                    var t = m.GetTexture(slot);
                    if (t == null) continue;
                    string file;
                    if (!written.TryGetValue(t.GetInstanceID(), out file))
                    {
                        file = "tex_" + written.Count.ToString("00", Inv) + "_" + Safe(t.name) + ".png";
                        var png = Png(t, slot == "_BumpMap" || slot == "_GlossTex");
                        if (png != null) FileManagerSecure.WriteAllBytes(dir + "/" + file, png);
                        written[t.GetInstanceID()] = file;
                        yield return null;
                    }
                    mats.Append('\t').Append(slot).Append('=').Append(file);
                }
            mats.Append('\n');
        }
        FileManagerSecure.WriteAllText(dir + "/materials.txt", mats.ToString());

        // The appearance's own texture files (originals, full quality), for reference.
        var urls = new StringBuilder();
        if (tc != null)
            foreach (var region in new[] { "face", "torso", "limbs", "genitals" })
                foreach (var kind in new[] { "Diffuse", "Specular", "Gloss", "Normal", "Detail", "Decal" })
                {
                    string u = null;
                    try { u = tc.GetUrlParamValue(region + kind + "Url"); } catch { }
                    if (!string.IsNullOrEmpty(u)) urls.Append(region).Append(kind).Append('\t').Append(u).Append('\n');
                }
        FileManagerSecure.WriteAllText(dir + "/urls.txt", urls.ToString());

        yield return StartCoroutine(Thumb(dir + "/thumb.jpg", selector));
        FileManagerSecure.WriteAllText(dir + "/info.txt", "character\t" + character.displayName + "\natom\t" + containingAtom.uid +
            "\nvertices\t" + verts.Length + " (UV vertices; base " + mesh.numBaseVertices + ")\nmaterials\t" + (matNames != null ? matNames.Length : 0) +
            "\nsource\t" + source + "\ntextures\t" + written.Count + "\nstretched faces\t" + stretched + "\n");
        Say("done: " + verts.Length + " vertices, " + written.Count + " textures -> " + dir + ". Check: " +
            (stretched == 0 ? "body.obj looks intact" : stretched + " stretched faces in body.obj - the morph snapshot may be incomplete; press Export again"));
        _busy = false;
    }

    // ---------------------------------------------------------------- morph deltas

    // Morph names to export: morphs.cfg (one display name per line; '#' comments; "prefix <text>" adds every
    // morph whose name starts with <text>), else the built-in defaults.
    static readonly string[] DefaultNames = {
        "Bodybuilder Details", "Torso Fitness", "Fitness Size", "Body Tone", "(REN) Thin", "Chubby 3", "Waist Width",
        "Hip Size", "Thighs Size", "HipThighYoung", "Breast Zero", "Breast Small", "Breasts Small", "Breast Large",
        "Breasts Natural", "Breasts TogetherApart", "Breast Sag1", "Breast Height Lower" };
    const string DefaultPrefix = "pJCM";

    void LoadList(List<string> names, List<string> prefixes, out string source)
    {
        source = "built-in defaults";
        try
        {
            if (FileManagerSecure.FileExists(CfgPath))
            {
                foreach (var raw in FileManagerSecure.ReadAllText(CfgPath).Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line.StartsWith("prefix ", StringComparison.OrdinalIgnoreCase)) { prefixes.Add(line.Substring(7).Trim()); continue; }
                    names.Add(line);
                }
                source = CfgPath;
                return;
            }
        }
        catch (Exception e) { SuperController.LogError("SF export: " + CfgPath + ": " + e.Message + " (using defaults)"); names.Clear(); prefixes.Clear(); }
        names.AddRange(DefaultNames);
        prefixes.Add(DefaultPrefix);
    }

    // One snapshot of the morphed UV vertices: sets _snap (a CLONE, VaM may reuse its array) or leaves it null on timeout.
    Vector3[] _snap;
    IEnumerator Snap(DAZCharacterRun run)
    {
        _snap = null;
        var before = run.snappedMorphedUVVertices;
        yield return null; // let the (threaded) morph update see the new values first
        run.doSnap = true;
        for (int f = 1; f <= 60; f++)
        {
            yield return null;
            var cur = run.snappedMorphedUVVertices;
            if (cur != null && (!ReferenceEquals(cur, before) || (!run.doSnap && f >= 2)))
            {
                _snap = (Vector3[])cur.Clone();
                yield break;
            }
        }
    }

    static float MaxDist(Vector3[] a, Vector3[] b)
    {
        float m = 0f;
        for (int i = 0; i < a.Length; i++) { float d = (a[i] - b[i]).sqrMagnitude; if (d > m) m = d; }
        return Mathf.Sqrt(m);
    }

    class PoseRec { public DAZMorph m; public float v; }

    IEnumerator RunMorphs()
    {
        _busy = true;
        // State to put back whatever happens.
        var poseRecs = new List<PoseRec>();
        JSONStorableBool blink = null;
        bool blinkWas = false;
        DAZMorph probing = null; float probingV = 0f, probingMin = 0f, probingMax = 0f;
        try
        {
            var selector = containingAtom.GetComponentInChildren<DAZCharacterSelector>();
            var character = selector != null ? selector.selectedCharacter : null;
            if (character == null) { Say("no character selected"); yield break; }
            DAZSkinV2 skin = character.GetComponentInChildren<DAZMergedSkinV2>();
            if (skin == null) skin = character.skin;
            var mesh = skin != null ? skin.dazMesh : null;
            var run = containingAtom.GetComponentInChildren<DAZCharacterRun>();
            var ui = selector.morphsControlUI;
            if (mesh == null || run == null || ui == null) { Say("character has no skin mesh / character run / morph list yet"); yield break; }
            int nv = mesh.numUVVertices;

            // Names: cfg or defaults, plus every morph under a prefix (pJCM).
            var names = new List<string>(); var prefixes = new List<string>(); string listSource;
            LoadList(names, prefixes, out listSource);
            var all = ui.GetMorphs();
            var seen = new HashSet<string>(names);
            if (all != null && prefixes.Count > 0)
            {
                var found = new List<string>();
                foreach (var m in all)
                {
                    if (m == null) continue;
                    string n = m.displayName;
                    if (string.IsNullOrEmpty(n) || seen.Contains(n)) continue;
                    foreach (var pre in prefixes)
                        if (n.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) { found.Add(n); seen.Add(n); break; }
                }
                found.Sort(StringComparer.Ordinal);
                names.AddRange(found);
            }

            // Stable base: pose-control morphs to zero (restored in the finally), eyes open.
            if (all != null)
                foreach (var m in all)
                    if (m != null && m.isPoseControl && m.morphValue != 0f) { poseRecs.Add(new PoseRec { m = m, v = m.morphValue }); m.morphValue = 0f; }
            blink = FindBlink();
            blinkWas = blink != null && blink.val;
            if (blinkWas) { blink.val = false; Progress("eyes open for the base ..."); yield return new WaitForSeconds(0.8f); }

            Progress("snapping the base (twice) ...");
            yield return StartCoroutine(Snap(run));
            var baseA = _snap;
            for (int i = 0; i < 10; i++) yield return null;
            yield return StartCoroutine(Snap(run));
            var baseB = _snap;
            if (baseA == null || baseB == null) { Say("morph export aborted: the character run did not deliver a snapshot within 60 frames"); yield break; }
            if (baseA.Length != nv || baseB.Length != nv) { Say("morph export aborted: snapshot has " + baseA.Length + " vertices, mesh has " + nv); yield break; }
            float noise = MaxDist(baseA, baseB) * 1000f;
            if (noise > 0.1f)
            {
                Say("morph export aborted: the base changes by " + noise.ToString("0.###", Inv) + " mm between two snapshots (limit 0.1). " +
                    "Breathing, expressions or BetterBends are moving the morphs: pause them (and animation) and press the button again.");
                yield break;
            }
            var baseline = baseA;

            // Output folder: the last body.obj export of this atom, else a new one (with the base as body.obj).
            string dir = _lastDir;
            if (dir == null || !FileManagerSecure.FileExists(dir + "/body.obj"))
            {
                dir = "Saves/PluginData/SfExport/" + Safe(containingAtom.uid) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv);
                FileManagerSecure.CreateDirectory(dir);
                _lastDir = dir;
                FileManagerSecure.WriteAllText(dir + "/body.obj", Obj("VaM " + character.displayName + " morph export base (pose-control morphs zeroed)",
                    baseline, run.snappedMorphedUVNormals, mesh.UV, mesh.UVPolyList, mesh.materialNames));
            }
            FileManagerSecure.CreateDirectory(dir + "/morphs");

            var rows = new StringBuilder("# name\tvalue\tprobe value\tisPoseControl\tregion\tstatus\t|D| max mm\tnonzero\n");
            int written = 0;
            for (int k = 0; k < names.Count; k++)
            {
                string name = names[k];
                Progress("morph " + (k + 1) + " of " + names.Count + ": " + name);
                DAZMorph m = null;
                try { m = ui.GetMorphByDisplayName(name); } catch (Exception e) { SuperController.LogError("SF export: " + name + ": " + e.Message); }
                if (m == null)
                {
                    SuperController.LogMessage("SF export: morph '" + name + "' not found, skipped");
                    rows.Append(name).Append("\t-\t-\t-\t-\tmissing\t-\t-\n");
                    continue;
                }
                bool isJcm = name.StartsWith("pJCM", StringComparison.OrdinalIgnoreCase);
                float v = m.morphValue;
                string pc = m.isPoseControl ? "1" : "0", region = m.resolvedRegionName;
                probing = m; probingV = v; probingMin = m.min; probingMax = m.max;
                // Widen the limits if the probe value is outside them (put back right after the snap).
                float target = isJcm ? 1f : v + 1f;
                if (target > m.max) m.max = target;
                if (target < m.min) m.min = target;
                m.morphValue = target;
                float probe = m.morphValue;
                float step = probe - v;
                if (Mathf.Abs(step) < 0.01f)
                {
                    SuperController.LogError("SF export: morph '" + name + "' would not move (value " + F(v) + ", set " + F(probe) + "), skipped");
                    m.morphValue = v; m.min = probingMin; m.max = probingMax; probing = null;
                    rows.Append(name).Append('\t').Append(F(v)).Append("\t-\t").Append(pc).Append('\t').Append(region).Append("\tskipped (cannot move)\t-\t-\n");
                    continue;
                }
                yield return StartCoroutine(Snap(run));
                var probed = _snap;
                m.morphValue = v; m.min = probingMin; m.max = probingMax; probing = null;
                if (probed == null || probed.Length != nv)
                {
                    SuperController.LogError("SF export: no snapshot for '" + name + "', skipped");
                    rows.Append(name).Append('\t').Append(F(v)).Append('\t').Append(F(probe)).Append('\t').Append(pc).Append('\t').Append(region).Append("\tskipped (no snapshot)\t-\t-\n");
                    continue;
                }

                var sb = new StringBuilder(8192);
                sb.Append("# name ").Append(name).Append('\n')
                  .Append("# probe step ").Append(F(step)).Append(" (value ").Append(F(v)).Append(" -> ").Append(F(probe)).Append(")\n")
                  .Append("# vertices ").Append(nv).Append(" (UV vertices, body.obj order)\n")
                  .Append("# i dx dy dz: 0-based UV vertex index, delta in metres, Unity axes, as body.obj; |d| < 0.05 mm dropped\n");
                int nz = 0; float maxD = 0f;
                for (int i = 0; i < nv; i++)
                {
                    var d = probed[i] - baseline[i];
                    float mag = d.magnitude;
                    if (mag > maxD) maxD = mag;
                    if (mag < 0.00005f) continue;
                    nz++;
                    sb.Append(i).Append(' ').Append(V(d)).Append('\n');
                }
                FileManagerSecure.WriteAllText(dir + "/morphs/" + Safe(name) + ".delta", sb.ToString());
                written++;
                rows.Append(name).Append('\t').Append(F(v)).Append('\t').Append(F(probe)).Append('\t').Append(pc).Append('\t').Append(region)
                    .Append("\tfound\t").Append((maxD * 1000f).ToString("0.###", Inv)).Append('\t').Append(nz).Append('\n');
            }
            FileManagerSecure.WriteAllText(dir + "/morphs.txt", rows.ToString());

            // Re-snap the base: how far did the base wander while we probed?
            for (int i = 0; i < 10; i++) yield return null;
            yield return StartCoroutine(Snap(run));
            string drift;
            if (_snap != null && _snap.Length == nv)
            {
                float dr = MaxDist(baseline, _snap) * 1000f;
                drift = "max " + dr.ToString("0.###", Inv) + " mm" + (dr > 0.1f ? " (above 0.1 mm: check that nothing was moving morphs)" : " (ok)");
            }
            else drift = "re-snap failed";
            string info = dir + "/info.txt", old = "";
            try { if (FileManagerSecure.FileExists(info)) old = FileManagerSecure.ReadAllText(info); } catch { }
            if (old.Length > 0 && !old.EndsWith("\n")) old += "\n";
            FileManagerSecure.WriteAllText(info, old + "morph export\t" + names.Count + " morphs from " + listSource + ", " + written + " written, base snapshot noise " +
                noise.ToString("0.###", Inv) + " mm\nbase drift\t" + drift + "\nbase pose-control morphs zeroed\t" + poseRecs.Count + "\n");
            Say("morphs done: " + written + " of " + names.Count + " written -> " + dir + "/morphs. Base drift " + drift);
        }
        finally
        {
            // Put everything back (also if the coroutine was cut short).
            try
            {
                if (probing != null) { probing.morphValue = probingV; probing.min = probingMin; probing.max = probingMax; }
                foreach (var r in poseRecs) r.m.morphValue = r.v;
                if (blinkWas && blink != null) blink.val = true;
            }
            catch (Exception e) { SuperController.LogError("SF export: restoring morph values: " + e.Message); }
            _busy = false;
        }
    }

    // Faces with an edge longer than 10 cm: Genesis 2's longest edges are a few cm, so any such face means
    // vertices in the wrong place (a torn mesh like the one the posed export gave).
    static int Stretched(Vector3[] v, MeshPoly[] polys)
    {
        int n = 0;
        if (polys == null) return 0;
        foreach (var poly in polys)
        {
            if (poly == null || poly.vertices == null) continue;
            var p = poly.vertices;
            for (int i = 0; i < p.Length; i++)
            {
                int a = p[i], b = p[(i + 1) % p.Length];
                if (a < v.Length && b < v.Length && (v[a] - v[b]).sqrMagnitude > 0.01f) { n++; break; }
            }
        }
        return n;
    }

    static string Region(DAZCharacterTextureControl tc, int i)
    {
        if (tc == null) return "-";
        if (Array.IndexOf(tc.faceMaterialNums ?? new int[0], i) >= 0) return "face";
        if (Array.IndexOf(tc.torsoMaterialNums ?? new int[0], i) >= 0) return "torso";
        if (Array.IndexOf(tc.limbMaterialNums ?? new int[0], i) >= 0) return "limbs";
        if (Array.IndexOf(tc.genitalMaterialNums ?? new int[0], i) >= 0) return "genitals";
        return "-";
    }

    static string Obj(string title, Vector3[] v, Vector3[] n, Vector2[] uv, MeshPoly[] polys, string[] matNames)
    {
        var sb = new StringBuilder(v.Length * 64);
        sb.Append("# ").Append(title).Append("; Unity axes (left-handed, Y up), metres\n");
        foreach (var p in v) sb.Append("v ").Append(V(p)).Append('\n');
        bool hasN = n != null && n.Length == v.Length, hasUv = uv != null && uv.Length == v.Length;
        if (hasN) foreach (var p in n) sb.Append("vn ").Append(V(p)).Append('\n');
        if (hasUv) foreach (var p in uv) sb.Append("vt ").Append(F(p.x)).Append(' ').Append(F(p.y)).Append('\n');
        if (polys == null) return sb.ToString();
        // Group faces by material, keeping each material's faces in VaM's order.
        var byMat = new SortedDictionary<int, List<MeshPoly>>();
        foreach (var poly in polys)
        {
            if (poly == null || poly.vertices == null) continue;
            List<MeshPoly> l;
            if (!byMat.TryGetValue(poly.materialNum, out l)) { l = new List<MeshPoly>(); byMat[poly.materialNum] = l; }
            l.Add(poly);
        }
        foreach (var kv in byMat)
        {
            sb.Append("g ").Append(matNames != null && kv.Key < matNames.Length ? Safe(matNames[kv.Key]) : "mat" + kv.Key).Append('\n');
            foreach (var poly in kv.Value)
            {
                sb.Append('f');
                foreach (var i in poly.vertices)
                {
                    int x = i + 1;
                    sb.Append(' ').Append(x);
                    if (hasUv || hasN) sb.Append('/').Append(hasUv ? x.ToString(Inv) : "");
                    if (hasN) sb.Append('/').Append(x);
                }
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    // GPU textures aren't readable: draw them into a render texture and read that back.
    static byte[] Png(Texture t, bool linear)
    {
        try
        {
            var rt = RenderTexture.GetTemporary(t.width, t.height, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            Graphics.Blit(t, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false, linear);
            tex.ReadPixels(new Rect(0, 0, t.width, t.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var bytes = ImageConversion.EncodeToPNG(tex);
            Destroy(tex);
            return bytes;
        }
        catch (Exception e) { SuperController.LogError("SF export: texture " + t.name + ": " + e.Message); return null; }
    }

    // A portrait from in front of the head, rendered by a temporary camera.
    IEnumerator Thumb(string path, DAZCharacterSelector selector)
    {
        Transform head = null;
        foreach (var t in containingAtom.GetComponentsInChildren<Transform>()) if (t.name == "head") { head = t; break; }
        if (head == null) yield break;
        var go = new GameObject("SfExportThumbCam");
        var cam = go.AddComponent<Camera>();
        var fwd = containingAtom.mainController != null ? containingAtom.mainController.transform.forward : Vector3.forward;
        cam.transform.position = head.position + fwd * 0.55f + Vector3.up * 0.03f;
        cam.transform.LookAt(head.position + Vector3.up * 0.03f);
        cam.fieldOfView = 30f;
        cam.nearClipPlane = 0.05f;
        var rt = RenderTexture.GetTemporary(512, 512, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        yield return new WaitForEndOfFrame();
        cam.Render();
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(512, 512, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, 512, 512), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        cam.targetTexture = null;
        RenderTexture.ReleaseTemporary(rt);
        FileManagerSecure.WriteAllBytes(path, ImageConversion.EncodeToJPG(tex, 90));
        Destroy(tex);
        Destroy(go);
    }

    static string F(float f) { return f.ToString("0.######", Inv); }
    static string V(Vector3 p) { return F(p.x) + " " + F(p.y) + " " + F(p.z); }

    static string Safe(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s ?? "") sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
        return sb.ToString();
    }
}
