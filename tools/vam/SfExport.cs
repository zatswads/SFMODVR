// SF character export: a Virt-A-Mate plugin (add it to a Person atom) that writes the
// character as VaM shows it, for the SF Body Morphs character pipeline (docs/PIPELINE.md):
//   body.obj        Genesis 2 mesh with every morph applied, NOT posed (rest frame, metres,
//                   Unity axes), UV vertices with UVs, one group per material
//   joints.txt      every DAZ bone: name, parent, morphed rest position, posed position
//   materials.txt   material index, name, enabled, region, and the PNG written per texture slot
//   urls.txt        the texture files the appearance uses (face/torso/limbs/genitals)
//   tex_*.png       the textures VaM renders with (diffuse, specular, gloss, normal, decal)
//   thumb.jpg       a portrait
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
        CreateTextField(_status);
    }

    void Say(string s) { if (_status != null) _status.val = s; SuperController.LogMessage("SF export: " + s); }

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
        Say("exporting to " + dir + " ...");

        // Morphed, unposed vertices: ask VaM's character runner for a snapshot (what VaM's own OBJ
        // export does), so the threaded morph update can't hand us a half-updated array.
        Vector3[] verts = null, normals = null;
        // Auto blink drives the eyelid morphs: a snapshot mid-blink gives the character half-closed eyes for good.
        // Blink off and the lids open again for the snapshot, then back as it was.
        JSONStorableBool blink = null;
        foreach (var id in containingAtom.GetStorableIDs())
        {
            var st = containingAtom.GetStorableByID(id);
            var b = st != null ? st.GetBoolJSONParam("blinkEnabled") : null;
            if (b != null) { blink = b; break; }
        }
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
