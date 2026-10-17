using System.Globalization;
using UnityEngine;

namespace SfMorph
{
    // 0.11.31 build triangle (user: a Skinny / Strength / Fat triangle like Saints Row's build screen, as a shortcut): picking a point sets the
    // sliders it stands for, nothing else. Fat = Chubby above 0, Skinny = Chubby below 0 (slimmer in the same areas), Strength = the muscle
    // tone (Fitness), the tensed muscles' bulge (Flex) and the relief's depth. The centre is her as she is by default. Every slider stays
    // free afterwards; the dot only shows the last pick.
    internal partial class Panel
    {
        Texture2D _triTex, _dotTex;
        bool _triDrag;
        const float TriW = 220f, TriH = 190f;

        void BuildSection(Character c)
        {
            GUILayout.Label("Build", _bold);
            string off = !_p.GpuContactOn.Value ? "GPU contact is off (Physics tab): only the muscle relief follows" : null;
            if (_triTex == null) MakeTriTextures();
            float ws = Mathf.Clamp01(_p.BuildStrength.Value), wf = Mathf.Clamp01(_p.BuildFat.Value);
            if (ws + wf > 1f) { float k = 1f / (ws + wf); ws *= k; wf *= k; }
            float wk = 1f - ws - wf;
            GUILayout.BeginHorizontal();
            GUILayout.Space(30);
            Rect r = GUILayoutUtility.GetRect(TriW, TriH + 36f, GUILayout.Width(TriW), GUILayout.Height(TriH + 36f));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            // corners: top Strength, bottom left Skinny, bottom right Fat
            Rect tri = new Rect(r.x, r.y + 18f, TriW, TriH);
            Vector2 top = new Vector2(tri.x + TriW * 0.5f, tri.y), bl = new Vector2(tri.x, tri.yMax), br = new Vector2(tri.xMax, tri.yMax);
            if (Event.current.type == EventType.Repaint) GUI.DrawTexture(tri, _triTex);
            GUI.Label(new Rect(top.x - 60f, r.y, 120f, 18f), "Strength " + (ws * 100f).ToString("0", CultureInfo.InvariantCulture) + " %", _bold);
            GUI.Label(new Rect(bl.x - 10f, tri.yMax + 2f, 120f, 18f), "Skinny " + (wk * 100f).ToString("0", CultureInfo.InvariantCulture) + " %", _bold);
            GUI.Label(new Rect(br.x - 80f, tri.yMax + 2f, 120f, 18f), "Fat " + (wf * 100f).ToString("0", CultureInfo.InvariantCulture) + " %", _bold);
            Vector2 dot = top * ws + bl * wk + br * wf;
            if (Event.current.type == EventType.Repaint) GUI.DrawTexture(new Rect(dot.x - 7f, dot.y - 7f, 14f, 14f), _dotTex);
            // pick: a click inside (or near) the triangle, then a drag
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && tri.Contains(e.mousePosition)) { _triDrag = true; e.Use(); }
            if (_triDrag && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag))
            {
                float s, k, f;
                Bary(e.mousePosition, top, bl, br, out s, out k, out f);
                if (Mathf.Abs(s - ws) > 0.002f || Mathf.Abs(f - wf) > 0.002f) { _p.BuildStrength.Value = s; _p.BuildFat.Value = f; ApplyBuild(s, k, f); }
                e.Use();
            }
            if (e.type == EventType.MouseUp && _triDrag) { _triDrag = false; e.Use(); }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Centre (her default)", GUILayout.Width(160))) { _p.BuildStrength.Value = _p.BuildFat.Value = 1f / 3f; ApplyBuild(1f / 3f, 1f / 3f, 1f / 3f); }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (off != null) GUILayout.Label("    " + off, _off);
            GUILayout.Label("A shortcut: picking a point sets Chubby (Fat above 0, Skinny below 0) and the muscle tone, Flex and relief depth (Strength) " +
                            "below. Adjust every slider freely afterwards; the dot shows the last pick.", _hint);
        }

        // 0.11.32 Debug tab: the fold test, the chubby test and the Delta Mush A/B (ShapeTest)
        void ShapeTests()
        {
            var gpu = _body.Gpu;
            string off = !_p.GpuContactOn.Value ? "GPU contact is off (Physics tab)" : gpu == null ? "shader bundle not loaded" : null;
            string foldOff = off ?? (!gpu.FoldKernels ? "the shader bundle is older than this DLL (install dist/sfmorph_shaders)" : null);
            int fi = ShapeTest.FoldIndex, ci = ShapeTest.FatIndex;
            GUILayout.BeginHorizontal();
            GUI.enabled = foldOff == null && gpu.FoldVerts > 0;
            if (fi < 0) { if (GUILayout.Button("Fold test (inner elbows, backs of the knees, groin: " + ShapeTest.FoldStep.ToString("0.0", CultureInfo.InvariantCulture) + " s each)")) ShapeTest.StartFold(); }
            else if (GUILayout.Button("Stop test", GUILayout.Width(90))) ShapeTest.Stop();
            GUI.enabled = true;
            if (fi >= 0) GUILayout.Label("now: " + ShapeTest.FoldNames[fi] + " at full (" + ShapeTest.FoldLeft.ToString("0.0", CultureInfo.InvariantCulture) + " s)", _hint);
            GUILayout.EndHorizontal();
            if (foldOff != null) GUILayout.Label("    Fold test off: " + foldOff, _off);
            else if (gpu.FoldVerts == 0) GUILayout.Label("    Fold test off: no fold on her baked body meshes (yet)", _off);
            GUILayout.BeginHorizontal();
            GUI.enabled = foldOff == null && gpu.FatVerts > 0;
            if (ci < 0) { if (GUILayout.Button("Chubby test (each area alone, then all slimmer, all fuller: " + ShapeTest.FatStep.ToString("0.0", CultureInfo.InvariantCulture) + " s each)")) ShapeTest.StartFat(); }
            else if (GUILayout.Button("Stop test", GUILayout.Width(90))) ShapeTest.Stop();
            GUI.enabled = true;
            if (ci >= 0) GUILayout.Label("now: " + ShapeTest.FatNames[ci] + " (" + ShapeTest.FatLeft.ToString("0.0", CultureInfo.InvariantCulture) + " s)", _hint);
            GUILayout.EndHorizontal();
            if (foldOff == null && gpu.FatVerts == 0) GUILayout.Label("    Chubby test off: no chubby area on her baked meshes (yet)", _off);
            string mushOff = off ?? (gpu.MushNote.Length > 0 ? gpu.MushNote : null);
            GUILayout.BeginHorizontal();
            GUI.enabled = mushOff == null;
            bool? mo = ShapeTest.MushOverride;
            if (mo == null) { if (GUILayout.Button("Round joints A/B (Delta Mush on / off every " + ShapeTest.MushHalf.ToString("0", CultureInfo.InvariantCulture) + " s for " + ShapeTest.MushRun.ToString("0", CultureInfo.InvariantCulture) + " s; watch a bent joint)")) ShapeTest.StartMush(); }
            else if (GUILayout.Button("Stop test", GUILayout.Width(90))) ShapeTest.Stop();
            GUI.enabled = true;
            if (mo != null) GUILayout.Label("now: " + (mo.Value ? "ON" : "off"), _bold);
            GUILayout.EndHorizontal();
            if (mushOff != null) GUILayout.Label("    Round joints A/B off: " + mushOff, _off);
            GUILayout.Label("    The tests run over whatever she is doing and put your settings back when they end; the log has one line per step.", _hint);
        }

        // Barycentric weights of p in the triangle (strength = top, skinny = bottom left, fat = bottom right), clamped into it.
        static void Bary(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out float wa, out float wb, out float wc)
        {
            Vector2 v0 = b - a, v1 = c - a, v2 = p - a;
            float d00 = Vector2.Dot(v0, v0), d01 = Vector2.Dot(v0, v1), d11 = Vector2.Dot(v1, v1), d20 = Vector2.Dot(v2, v0), d21 = Vector2.Dot(v2, v1);
            float den = d00 * d11 - d01 * d01;
            wb = (d11 * d20 - d01 * d21) / den;
            wc = (d00 * d21 - d01 * d20) / den;
            wa = 1f - wb - wc;
            wa = Mathf.Max(0f, wa); wb = Mathf.Max(0f, wb); wc = Mathf.Max(0f, wc);
            float sum = wa + wb + wc;
            if (sum < 1e-6f) { wa = wb = wc = 1f / 3f; return; }
            wa /= sum; wb /= sum; wc /= sum;
        }

        // The sliders a pick stands for. Centre (1/3 each) = Chubby 0, Fitness 0.3, Flex 1, relief 1 (the defaults).
        void ApplyBuild(float s, float k, float f)
        {
            float d = f - k;                                     // -1 skinny .. 1 fat
            _p.Chubby.Value = Mathf.Clamp(d > 0f ? 2f * d : d, -1f, 2f);
            float st = s - 1f / 3f;                              // -1/3 .. 2/3
            float u = st >= 0f ? st / (2f / 3f) : st / (1f / 3f);   // -1 .. 1
            _p.MuscleFitness.Value = Mathf.Clamp01(u >= 0f ? 0.3f + 0.7f * u : 0.3f * (1f + u));
            _p.MuscleFlexAmt.Value = Mathf.Clamp(u >= 0f ? 1f + u : 1f + 0.5f * u, 0f, 2f);
            _p.MuscleStrength.Value = Mathf.Clamp(u >= 0f ? 1f + 0.6f * u : 1f + 0.5f * u, 0f, 2f);
            if (u > 0.05f && !_p.MuscleOn.Value) _p.MuscleOn.Value = true;   // the strength corner shows muscle
        }

        void MakeTriTextures()
        {
            const int W = 128, H = 110;
            _triTex = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    // texture row 0 is the bottom: the apex at the top row
                    float fy = (H - 1 - y) / (float)(H - 1);
                    float w = 1f - fy, cx = (x + 0.5f) / W - 0.5f;
                    bool inside = Mathf.Abs(cx) <= 0.5f * w;
                    bool edge = inside && (Mathf.Abs(cx) > 0.5f * w - 0.02f || fy > 0.975f);
                    byte g = (byte)(150 + 60 * (1f - fy));
                    px[y * W + x] = !inside ? new Color32(0, 0, 0, 0) : edge ? new Color32(120, 200, 240, 255) : new Color32(g, g, (byte)Mathf.Min(255, g + 12), 235);
                }
            _triTex.SetPixels32(px);
            _triTex.Apply(false, true);
            _dotTex = new Texture2D(16, 16, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var dp = new Color32[256];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    float r = new Vector2(x - 7.5f, y - 7.5f).magnitude;
                    dp[y * 16 + x] = r > 7.5f ? new Color32(0, 0, 0, 0) : r > 5.5f ? new Color32(40, 40, 40, 255) : new Color32(120, 200, 240, 255);
                }
            _dotTex.SetPixels32(dp);
            _dotTex.Apply(false, true);
        }
    }
}
