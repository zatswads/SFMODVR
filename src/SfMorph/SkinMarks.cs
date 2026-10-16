using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace SfMorph
{
    // 0.8.0 hand prints and slap redness (design 3): GpuContact's per-vertex marks (x redness, y live pressure, z lingering dent, indexed by the
    // welded vertex id) are rasterised in UV space (SkinMarks.shader pass 0) into a marks texture per skin material, then composed into a copy of
    // her albedo (redness, pressed-flesh darkening, the pale print of a lifted hand) and, when Wrinkles does not own the normal slot, a copy of her
    // normal map (the lingering dent). With Wrinkles owning the normal slot (the usual case with Body wrinkles on) the marks go in through
    // Wrinkles' own blend pass: this class hands it the marks RT (MarksOf / SlopeScaleOf) and Touches its sheet after every raster. 0.8.4: the
    // same for the albedo: while Wrinkles keeps its albedo copy of the material (crease shade, debug view) its albedo pass composes the marks
    // (AlbedoParams) and this class keeps no albedo copy, so there is one albedo render texture per material.
    // Idle (nothing pressed or slapped for a while) puts the original textures back and frees every render texture: nothing of ours is in any
    // slot then. Update runs on the main thread (Body, after GpuStep); the raster runs in GpuContact.AfterImprint (render loop, no throwing).
    internal sealed class SkinMarks
    {
        public string Status = "";

        // True while a sheet is up (marks fading or pressed) or a slap is queued: GpuContact keeps the marks decay and the MarkDisc kernel running.
        public static bool Active { get { return (_cur != null && _cur._sheets.Count > 0 && _cur._anyBuilt) || _discN > 0; } }

        // ---- statics: slaps, lookups for Wrinkles / Morph ----

        static SkinMarks _cur;
        static bool _marksOn;                        // marks enabled and loaded (Slap queues only then)
        static readonly Vector4[] _discs = new Vector4[4];
        static readonly float[] _discAmt = new float[4];
        static int _discN, _slapCount;
        static float _slapAt = -100f;
        static readonly Func<Material, string, Texture> _baseFn = BaseOf;

        // A slap mark at a world point (Reactions, tier >= 3): queued (4 at most, the oldest dropped); GpuContact's MarkDisc kernel adds it.
        public static void Slap(Vector3 worldPoint, float radius, float intensity)
        {
            if (!_marksOn || !(intensity > 0f) || !(radius > 0f)) return;
            if (float.IsNaN(worldPoint.x) || float.IsNaN(worldPoint.y) || float.IsNaN(worldPoint.z)) return;
            if (_discN == _discs.Length)
            {
                for (int i = 1; i < _discs.Length; i++) { _discs[i - 1] = _discs[i]; _discAmt[i - 1] = _discAmt[i]; }
                _discN--;
            }
            float amt = Mathf.Clamp01(intensity) * 0.9f;
            _discs[_discN] = new Vector4(worldPoint.x, worldPoint.y, worldPoint.z, radius);
            _discAmt[_discN] = amt;
            _discN++;
            _slapAt = Time.time;
            _slapCount++;
            if (_slapCount <= 10 || _slapCount % 10 == 0)
                Plugin.Log.LogInfo("Skin marks: slap mark #" + _slapCount + " at (" + worldPoint.x.ToString("0.00") + ", " + worldPoint.y.ToString("0.00") + ", " + worldPoint.z.ToString("0.00")
                    + "), radius " + radius.ToString("0.00") + " m, redness +" + amt.ToString("0.00"));
        }

        // GpuContact.Run: the queued discs since the last call (xyz world centre, w radius; amt = added redness 0..1). Returns the count.
        public static int TakeDiscs(Vector4[] disc, float[] amt)
        {
            int n = Math.Min(_discN, Math.Min(disc != null ? disc.Length : 0, amt != null ? amt.Length : 0));
            for (int i = 0; i < n; i++) { disc[i] = _discs[i]; amt[i] = _discAmt[i]; }
            _discN = 0;
            return n;
        }

        // Wrinkles' pass 1 adds the marks' dent to its normal blend: this material's marks RT (null when none) and 1 / metres per UV (0 when none).
        internal static RenderTexture MarksOf(Material m)
        {
            var sh = SheetOf(m);
            return sh != null && sh.MarksRT != null && sh.MarksRT.IsCreated() ? sh.MarksRT : null;
        }

        internal static float SlopeScaleOf(Material m)
        {
            var sh = SheetOf(m);
            return sh != null && sh.Mpu > 1e-4f ? 1f / sh.Mpu : 0f;
        }

        // Wrinkles.ExternalBase / Morph (through Wrinkles.BaseOf): what is under our render texture in this material's slot (null when not ours).
        internal static Texture BaseOf(Material m, string prop)
        {
            var sh = SheetOf(m);
            if (sh == null || string.IsNullOrEmpty(prop)) return null;
            if (prop == sh.NormalProp && sh.NormalRT != null) return Usable(sh.NormalBase);
            if (prop == sh.AlbedoProp && sh.AlbedoRT != null && sh.AlbedoHeld) return Usable(sh.AlbedoBase);
            return null;
        }

        // Wrinkles' albedo pass composes the marks with these (SkinMarks.shader pass 1's _Redness / _Ao); false when marks are off.
        internal static bool AlbedoParams(out float redness, out float ao)
        {
            redness = 0f; ao = 0f;
            var c = _cur;
            if (c == null || c._set == null || !_marksOn) return false;
            redness = c._set.Redness;
            ao = 0.35f * c._set.Ao;
            return true;
        }

        static Sheet SheetOf(Material m)
        {
            var c = _cur;
            if (c == null || m == null) return null;
            var l = c._sheets;
            for (int i = 0; i < l.Count; i++) if (l[i].Mat == m && l[i].Built) return l[i];
            return null;
        }

        static Texture Usable(Texture t) { return t != null && !Wrinkles.Owns(t) ? t : null; }

        // ---- state ----

        sealed class Rend
        {
            public SkinnedMeshRenderer R;
            public int Sub;
            public string Name;
            public ContactBake Bake;
            public GraphicsBuffer Tris, Uv, Canon, Marks;   // Tris / Uv are ours; Canon / Marks belong to GpuContact (re-queried after a BakeVersion bump)
            public int TriCount;
            public bool Seen;
            public readonly MaterialPropertyBlock Mpb = new MaterialPropertyBlock();
            public bool Ok { get { return R != null && Tris != null && Uv != null && Canon != null && Marks != null && TriCount > 0; } }
        }

        sealed class Sheet
        {
            public Material Mat;
            public string Name, AlbedoProp, NormalProp;
            public Texture AlbedoBase, AlbedoOrig, NormalBase, NormalOrig;   // the textures under ours (the game's, or Morph's baked ones); Orig = hers
            public RenderTexture MarksRT, AlbedoRT, NormalRT;
            public bool Built, Dirty, AlbedoHeld, ViaWrinkles, AlbedoViaWrinkles, LoggedProps;
            public float RetryAt, NextAlbedoTry;
            public float Mpu = 1f;                      // metres per UV (vertex-weighted mean over the renderers)
            public float RetakeAt;                      // after a skin toggle: when to take the albedo slot back
            public Texture LastNormalFor;
            public string Note = "";
            public readonly List<Rend> Rends = new List<Rend>();
            public readonly MaterialPropertyBlock MpbA = new MaterialPropertyBlock(), MpbN = new MaterialPropertyBlock();
        }

        static readonly string[] NormalProps = { "_NormalMap", "_BumpMap" };
        // Wrinkles' list plus the extra names Morph.cs knows (the game's skin is a shader graph: the property is not certain)
        static readonly string[] AlbedoProps = BuildAlbedoProps();
        static string[] BuildAlbedoProps()
        {
            var l = new List<string>(Wrinkles.AlbedoProps);
            foreach (var x in new[] { "_Albedo", "_DiffuseMap", "_Diffuse" }) if (!l.Contains(x)) l.Add(x);
            return l.ToArray();
        }

        static readonly int PFlip = Shader.PropertyToID("_FlipY"), PTestMode = Shader.PropertyToID("_TestMode"), PTest = Shader.PropertyToID("_Test"),
            PRedness = Shader.PropertyToID("_Redness"), PAo = Shader.PropertyToID("_Ao"), PMarksScale = Shader.PropertyToID("_MarksScale"),
            PBaseAlbedo = Shader.PropertyToID("_BaseAlbedo"), PBaseNormal = Shader.PropertyToID("_BaseNormal"), PMarksTex = Shader.PropertyToID("_MarksTex"),
            PMarksTexel = Shader.PropertyToID("_MarksTexel"), PTris = Shader.PropertyToID("_Tris"), PUv = Shader.PropertyToID("_Uv"),
            PCanon = Shader.PropertyToID("_Canon"), PMarkBuf = Shader.PropertyToID("_MarkBuf");

        const int MaxRT = 2048;

        readonly List<Sheet> _sheets = new List<Sheet>();
        readonly List<SkinnedMeshRenderer> _tmp = new List<SkinnedMeshRenderer>();
        readonly HashSet<string> _logged = new HashSet<string>();
        readonly StringBuilder _sb = new StringBuilder();
        Shader _shader;
        Material _mat;
        CommandBuffer _cmd;
        GpuContact _gpu;
        Character _her;
        ContactSettings _set = new ContactSettings();
        RenderTexture _testA, _testB;
        MaterialPropertyBlock _mpbTestA = new MaterialPropertyBlock(), _mpbTestB = new MaterialPropertyBlock();
        float _flip = -1f;
        int _flipState, _flipTries;                   // 0 = not run, 1 = readback pending, 2 = settled
        bool _bundleHeld, _subscribed, _loadFailed, _dead, _anyBuilt;
        string _why = "";
        int _ver = -1, _updateCount, _lastRasterFrame = -100, _rasters;
        float _lastActivity = -1000f, _lastRasterT, _nextDiscover, _nextStatus;

        // ---- Update (main thread) ----

        // Main thread, every frame after GpuStep (Body.ApplyAll).
        public void Update(Character her, ContactSettings s, GpuContact gpu, float now)
        {
            try { UpdateInner(her, s, gpu, now); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Skin marks: update failed, turned off: " + e);
                GoDead("error");
                try { Teardown(); } catch { }
            }
        }

        void UpdateInner(Character her, ContactSettings s, GpuContact gpu, float now)
        {
            _cur = this;
            if (s == null || !s.Marks || her == null || gpu == null)
            {
                string why = s == null || !s.Marks ? "marks are off" : "no GPU contact";
                if (_sheets.Count > 0 || _shader != null || _marksOn) { Teardown(); Plugin.Log.LogInfo("Skin marks: off (" + why + "), slots restored"); }
                _marksOn = false; _discN = 0;
                _loadFailed = false; _dead = false;
                Status = "Skin marks: off: " + why;
                return;
            }
            _her = her; _set = s; _gpu = gpu;
            if (_dead) { if (_shader != null || _sheets.Count > 0) Teardown(); Status = "Skin marks: off: " + _why; _marksOn = false; _discN = 0; return; }
            if (_shader == null && !Load()) { Status = "Skin marks: off: " + _why; _marksOn = false; _discN = 0; return; }
            _marksOn = true;
            EnsureFlip();
            if (_flipState != 2) { Status = "Skin marks: starting (orientation self-test)"; return; }
            _updateCount++;

            // activity: a hand or body on her soft zones (prints belong on the fatty parts; GpuContact.Contact also fires for a hand near a shin), or a slap
            if (Touching(her)) _lastActivity = now;
            if (_slapAt > _lastActivity) _lastActivity = _slapAt;

            float hold = Math.Max(10f, 4.6f * s.MarkFade);   // redness is under 1 % after 4.6 time constants
            if (_sheets.Count == 0)
            {
                if (now - _lastActivity > 0.25f) { Status = "Skin marks: idle"; return; }
                if (now >= _nextDiscover) Discover(now);
            }
            else
            {
                if (now - _lastActivity > hold) { ReleaseSheets("idle"); Status = "Skin marks: idle"; return; }
                if (now >= _nextDiscover || gpu.BakeVersion != _ver) Discover(now);
            }
            bool service = (_updateCount & 3) == 0;
            _anyBuilt = false;
            for (int i = _sheets.Count - 1; i >= 0; i--)
            {
                var sh = _sheets[i];
                if (sh.Mat == null || sh.Rends.Count == 0) { RemoveSheet(i, "its material or renderers are gone"); continue; }
                if (!sh.Built) { if (now >= sh.RetryAt) TryBuild(sh, now); }
                else if (service) ServiceSheet(sh, now);
                if (sh.Built) _anyBuilt = true;
            }
            if (now >= _nextStatus) { _nextStatus = now + 0.5f; UpdateStatus(); }
        }

        static bool Touching(Character her)
        {
            var z = her.Soft;
            return z != null && (z.Touching("glute") || z.Touching("thigh") || z.Touching("breast") || z.Touching("belly"));
        }

        void UpdateStatus()
        {
            if (_sheets.Count == 0) { Status = "Skin marks: idle"; return; }
            _sb.Length = 0;
            int built = 0;
            for (int i = 0; i < _sheets.Count; i++) if (_sheets[i].Built) built++;
            _sb.Append("Skin marks: ").Append(built).Append(built == 1 ? " sheet (" : " sheets (");
            bool first = true;
            for (int i = 0; i < _sheets.Count; i++)
            {
                var sh = _sheets[i];
                if (!sh.Built) continue;
                if (!first) _sb.Append("; ");
                first = false;
                _sb.Append(sh.Name).Append(": ");
                _sb.Append(sh.AlbedoViaWrinkles ? "albedo via Wrinkles" : sh.AlbedoProp == null ? "albedo slot not found" : sh.AlbedoHeld ? "albedo" : "albedo waiting");
                _sb.Append(sh.ViaWrinkles ? ", normal via Wrinkles" : sh.NormalRT != null ? ", normal" : ", no normal");
            }
            _sb.Append(built > 0 ? "), active" : ")");
            Status = built > 0 ? _sb.ToString() : "Skin marks: preparing";
        }

        // ---- load / shader / orientation self-test ----

        bool Load()
        {
            if (_loadFailed) return false;
            try
            {
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11) { return Fail("the skin marks shader is Direct3D 11 only (this is " + SystemInfo.graphicsDeviceType + ")"); }
                string err;
                if (!ShaderBundle.Acquire(System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location), out err)) return Fail("shader bundle (" + err + ")");
                _bundleHeld = true;
                _shader = ShaderBundle.Load<Shader>("SkinMarks");
                if (_shader == null) { ShaderBundleRelease(); return Fail("bundle (no SkinMarks shader inside: rebuild the shader bundle)"); }
                if (!_shader.isSupported) { _shader = null; ShaderBundleRelease(); return Fail("SkinMarks shader not supported on " + SystemInfo.graphicsDeviceType); }
                _mat = new Material(_shader) { hideFlags = HideFlags.HideAndDontSave };
                _cmd = new CommandBuffer { name = "SfMorph skin marks" };
                GpuContact.AfterImprint += OnAfterImprint;
                _subscribed = true;
                Wrinkles.ExternalBase = _baseFn;
                _flip = -1f; _flipState = 0; _flipTries = 0;
                Plugin.Log.LogInfo("Skin marks: loaded on " + SystemInfo.graphicsDeviceType + " (hold " + Math.Max(10f, 4.6f * _set.MarkFade).ToString("0") + " s after the last touch)");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Skin marks: load failed: " + e);
                try { Teardown(); } catch { }
                return Fail("load error");
            }
        }

        bool Fail(string why)
        {
            _loadFailed = true;
            _why = why;
            Plugin.Log.LogWarning("Skin marks: off: " + why);
            return false;
        }

        void ShaderBundleRelease()
        {
            if (!_bundleHeld) return;
            _bundleHeld = false;
            try { ShaderBundle.Release(); } catch (Exception e) { Plugin.Log.LogWarning("Skin marks: releasing the shader bundle failed: " + e.Message); }
        }

        // A UV gradient is rendered through the same uv -> clip mapping the raster uses, then sampled back at v = 0.25 into a 1x1 target; the
        // texel is read back (orientation-proof): 0.25 = mapping consistent with texture sampling, 0.75 = flipped (same test as Wrinkles').
        void EnsureFlip()
        {
            if (_flipState != 0 || _mat == null) return;
            if (_testA == null)
            {
                _testA = new RenderTexture(16, 16, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "SfMorph marks test A", filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                _testB = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "SfMorph marks test B", filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
                _testA.Create(); _testB.Create();
            }
            _flipTries++;
            _mat.SetFloat(PFlip, _flip);
            _mpbTestA.SetFloat(PTestMode, 0f);
            _mpbTestB.SetFloat(PTestMode, 1f);
            _mpbTestB.SetTexture(PTest, _testA);
            var cmd = _cmd;
            cmd.Clear();
            cmd.SetRenderTarget(_testA);
            cmd.DrawProcedural(Matrix4x4.identity, _mat, 3, MeshTopology.Triangles, 3, 1, _mpbTestA);
            cmd.SetRenderTarget(_testB);
            cmd.DrawProcedural(Matrix4x4.identity, _mat, 3, MeshTopology.Triangles, 3, 1, _mpbTestB);
            Graphics.ExecuteCommandBuffer(cmd);
            _flipState = 1;
            float used = _flip;
            AsyncGPUReadback.Request(_testB, 0, TextureFormat.RGBA32, req => FlipDone(req, used));
        }

        void FlipDone(AsyncGPUReadbackRequest req, float used)
        {
            try
            {
                if (_dead || _testB == null) return;
                if (req.hasError) { Plugin.Log.LogWarning("Skin marks: _FlipY self-test readback failed; keeping _FlipY=" + _flip); _flipState = 2; return; }
                var d = req.GetData<byte>();
                float g = d.Length >= 2 ? d[1] / 255f : -1f;
                string verdict;
                if (Mathf.Abs(g - 0.25f) < 0.12f) { verdict = "consistent"; _flipState = 2; }
                else if (Mathf.Abs(g - 0.75f) < 0.12f && _flipTries < 3) { verdict = "flipped, switching"; _flip = -used; _flipState = 0; }
                else { verdict = "inconclusive, keeping"; _flipState = 2; }
                Plugin.Log.LogInfo("Skin marks: _FlipY self-test: UV gradient probed at v=0.25 read " + g.ToString("0.00") + " with _FlipY=" + used.ToString("0") + " -> " + verdict
                    + (_flipState == 2 ? " (_FlipY=" + _flip.ToString("0") + (Wrinkles.FlipSettled ? ", Wrinkles' is " + Wrinkles.FlipY.ToString("0") : "") + ")" : ""));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Skin marks: self-test callback failed, turned off: " + e);
                GoDead("error");
            }
        }

        // ---- discovery: which renderer slots feed which sheet ----

        Sheet GetSheet(Material mat)
        {
            for (int i = 0; i < _sheets.Count; i++) if (_sheets[i].Mat == mat) return _sheets[i];
            var sh = new Sheet { Mat = mat, Name = mat.name.Replace(" (Instance)", "").Trim() };
            _sheets.Add(sh);
            return sh;
        }

        // About every 2 s while active (and at once after a bake change): her GPU-contact renderers whose slot is body / neck skin, grouped by Material.
        void Discover(float now)
        {
            _nextDiscover = now + 2f;
            _ver = _gpu.BakeVersion;
            _gpu.MarkRenderers(_tmp);
            for (int i = 0; i < _sheets.Count; i++) for (int k = 0; k < _sheets[i].Rends.Count; k++) _sheets[i].Rends[k].Seen = false;
            for (int i = 0; i < _tmp.Count; i++)
            {
                var r = _tmp[i];
                if (r == null || r.sharedMesh == null) continue;
                ContactBake bake; GraphicsBuffer canon, marks;
                if (!_gpu.MarksOf(r, out bake, out canon, out marks) || bake == null || bake.Uv == null || bake.SubTris == null || canon == null || marks == null) continue;
                var mats = r.sharedMaterials;
                for (int sub = 0; sub < mats.Length && sub < bake.SubTris.Length; sub++)
                {
                    var mat = mats[sub];
                    var tris = bake.SubTris[sub];
                    if (mat == null || tris == null || tris.Length < 3) continue;
                    int region = Wrinkles.Classify(mat, r);
                    if (region < 1 || region > 3) continue;   // skin only (0.9.7: her face too): never clothes, eyes, hair
                    var sh = GetSheet(mat);
                    Rend rd = null;
                    for (int k = 0; k < sh.Rends.Count; k++) if (sh.Rends[k].R == r && sh.Rends[k].Sub == sub) { rd = sh.Rends[k]; break; }
                    if (rd == null)
                    {
                        rd = new Rend { R = r, Sub = sub, Name = r.name + (mats.Length > 1 ? "#" + sub : "") };
                        sh.Rends.Add(rd);
                        sh.Dirty = true;
                    }
                    SyncRend(rd, bake, canon, marks);
                    rd.Seen = true;
                }
            }
            for (int i = _sheets.Count - 1; i >= 0; i--)
            {
                var sh = _sheets[i];
                for (int k = sh.Rends.Count - 1; k >= 0; k--)
                    if (!sh.Rends[k].Seen) { ReleaseRend(sh.Rends[k]); sh.Rends.RemoveAt(k); sh.Dirty = true; }
                if (sh.Rends.Count == 0) RemoveSheet(i, "no renderers left");
                else
                {
                    float w = 0f, m = 0f;
                    for (int k = 0; k < sh.Rends.Count; k++)
                    {
                        var b = sh.Rends[k].Bake;
                        if (b == null || !(b.MetresPerUv > 1e-4f)) continue;
                        w += b.V; m += b.V * b.MetresPerUv;
                    }
                    sh.Mpu = w > 0f ? m / w : 1f;
                }
            }
            if (_sheets.Count == 0 && _logged.Add("nosheets|" + _tmp.Count))
                Plugin.Log.LogInfo("Skin marks: no skin material among " + _tmp.Count + " GPU-contact renderers with a bake yet");
        }

        void SyncRend(Rend rd, ContactBake bake, GraphicsBuffer canon, GraphicsBuffer marks)
        {
            if (rd.Bake != bake || rd.Tris == null || rd.Uv == null)
            {
                RelOwn(rd);
                var tris = bake.SubTris[rd.Sub];
                int n = (tris.Length / 3) * 3;
                rd.TriCount = n / 3;
                rd.Tris = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(3, n), 4);
                rd.Tris.SetData(tris);
                rd.Uv = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(1, bake.Uv.Length), 8);
                rd.Uv.SetData(bake.Uv);
                rd.Bake = bake;
                rd.Mpb.SetBuffer(PTris, rd.Tris);
                rd.Mpb.SetBuffer(PUv, rd.Uv);
            }
            if (rd.Canon != canon) { rd.Canon = canon; rd.Mpb.SetBuffer(PCanon, canon); }
            if (rd.Marks != marks) { rd.Marks = marks; rd.Mpb.SetBuffer(PMarkBuf, marks); }
        }

        static void RelOwn(Rend rd)
        {
            if (rd.Tris != null) { try { rd.Tris.Release(); } catch { } rd.Tris = null; }
            if (rd.Uv != null) { try { rd.Uv.Release(); } catch { } rd.Uv = null; }
        }

        static void ReleaseRend(Rend rd)
        {
            RelOwn(rd);
            rd.Canon = null; rd.Marks = null; rd.Bake = null;   // GpuContact's buffers: never released here
        }

        void RemoveSheet(int i, string why)
        {
            var sh = _sheets[i];
            bool wr = sh.ViaWrinkles || sh.AlbedoViaWrinkles;
            Material mat = sh.Mat;
            RestoreSheet(sh);
            for (int k = 0; k < sh.Rends.Count; k++) ReleaseRend(sh.Rends[k]);
            sh.Rends.Clear();
            _sheets.RemoveAt(i);
            if (wr && mat != null) Wrinkles.Touch(mat);
            Plugin.Log.LogInfo("Skin marks: sheet '" + sh.Name + "' dropped (" + why + ")");
        }

        // ---- sheets: build, service, restore ----

        // The albedo property of a skin material: the first of the usual names that exists and holds a texture that is not one of ours.
        // heldBy = a name that holds a render texture of someone else (Wrinkles' debug view).
        static string FindAlbedo(Material mat, out string heldBy)
        {
            heldBy = null;
            for (int i = 0; i < AlbedoProps.Length; i++)
            {
                var p = AlbedoProps[i];
                if (!mat.HasProperty(p)) continue;
                var t = mat.GetTexture(p);
                if (t == null) continue;
                if (Wrinkles.Owns(t)) { if (heldBy == null) heldBy = p; continue; }
                if (t.width < 8 || t.height < 8) continue;
                return p;
            }
            return null;
        }

        Texture NormalForOf(Material mat)
        {
            try { return _her != null && _her.Morph != null ? _her.Morph.NormalFor(mat) : null; }
            catch { return null; }
        }

        Texture OrigOf(Material mat, string prop)
        {
            try { return _her != null && _her.Morph != null ? Usable(_her.Morph.OriginalTexture(mat, prop)) : null; }
            catch { return null; }
        }

        static void Fit(ref int w, ref int h, int cap)
        {
            int mx = Math.Max(w, h);
            if (mx > cap) { float f = (float)cap / mx; w = Math.Max(64, Mathf.RoundToInt(w * f)); h = Math.Max(64, Mathf.RoundToInt(h * f)); }
            w = Math.Max(64, w); h = Math.Max(64, h);
        }

        static RenderTexture MakeRT(string name, int w, int h, RenderTextureFormat fmt, RenderTextureReadWrite rw, bool mips, Texture like)
        {
            var rt = new RenderTexture(w, h, 0, fmt, rw)
            {
                name = name, useMipMap = mips, autoGenerateMips = false, filterMode = mips ? FilterMode.Trilinear : FilterMode.Bilinear,
                wrapMode = mips ? TextureWrapMode.Repeat : TextureWrapMode.Clamp, anisoLevel = mips ? (like != null ? Math.Max(1, like.anisoLevel) : 4) : 0,
                hideFlags = HideFlags.HideAndDontSave
            };
            rt.Create();
            Wrinkles.Own(rt);
            return rt;
        }

        static void DestroyRT(ref RenderTexture rt)
        {
            if (rt == null) return;
            Wrinkles.Disown(rt);
            if (rt.IsCreated()) rt.Release();
            UnityEngine.Object.Destroy(rt);
            rt = null;
        }

        void TryBuild(Sheet sh, float now)
        {
            var mat = sh.Mat;
            bool anyRend = false;
            for (int i = 0; i < sh.Rends.Count; i++) if (sh.Rends[i].Ok) { anyRend = true; break; }
            if (!anyRend) return;

            sh.NormalProp = null;
            foreach (var p in NormalProps) if (mat.HasProperty(p)) { sh.NormalProp = p; break; }
            // 0.8.4: Wrinkles keeps an albedo copy of this material (crease shade): the marks go into it, no albedo copy here
            bool wa = Wrinkles.OwnsAlbedoSlot(mat);
            string heldBy = null;
            sh.AlbedoProp = wa ? null : FindAlbedo(mat, out heldBy);
            sh.AlbedoViaWrinkles = wa;
            if (sh.AlbedoProp == null && !wa && !sh.LoggedProps)
            {
                sh.LoggedProps = true;
                var sb = new StringBuilder("Skin marks: material '" + sh.Name + "' (" + mat.shader.name + "): no albedo slot found" + (heldBy != null ? " (" + heldBy + " holds a render texture of another system)" : "")
                    + "; texture properties:");
                try
                {
                    foreach (var pn in mat.GetTexturePropertyNames())
                    {
                        var t = mat.GetTexture(pn);
                        sb.Append(' ').Append(pn).Append('=').Append(t != null ? (string.IsNullOrEmpty(t.name) ? "<unnamed>" : t.name) + " " + t.width + "x" + t.height : "none");
                    }
                }
                catch (Exception e) { sb.Append(" (listing failed: " + e.Message + ")"); }
                Plugin.Log.LogWarning(sb.ToString());
            }

            // normal: Wrinkles' slot, or ours from the base under it
            bool wr = sh.NormalProp != null && Wrinkles.OwnsNormalSlot(mat);
            Texture nSlot = sh.NormalProp != null ? mat.GetTexture(sh.NormalProp) : null;
            Texture nf = NormalForOf(mat);
            Texture nBase = nf != null && !Wrinkles.Owns(nf) ? nf : Usable(nSlot);
            bool ownNormal = !wr && sh.NormalProp != null && nBase != null;
            if (sh.AlbedoProp == null && !wa && !wr && !ownNormal)
            {
                sh.RetryAt = now + 5f;
                if (_logged.Add("noslot|" + sh.Name)) Plugin.Log.LogWarning("Skin marks: material '" + sh.Name + "' has neither an albedo nor a normal map slot we can use; retrying every 5 s");
                return;
            }

            sh.Note = "";
            Texture aSlot = null;
            if (sh.AlbedoProp != null)
            {
                aSlot = mat.GetTexture(sh.AlbedoProp);
                sh.AlbedoBase = aSlot;
                sh.AlbedoOrig = OrigOf(mat, sh.AlbedoProp) ?? aSlot;
            }
            if (nBase != null) { sh.NormalBase = nBase; sh.NormalOrig = OrigOf(mat, sh.NormalProp) ?? nBase; }
            sh.ViaWrinkles = wr;

            int mw = 1024, mh = 1024;
            Texture sizeSrc = aSlot != null ? aSlot : wa ? AlbedoSlotTexture(mat) ?? nBase : nBase;
            if (sizeSrc != null) { mw = sizeSrc.width; mh = sizeSrc.height; }
            Fit(ref mw, ref mh, MaxRT);
            sh.MarksRT = MakeRT("SfMorph marks " + sh.Name, mw, mh, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear, false, null);
            if (aSlot != null)
            {
                int w = aSlot.width, h = aSlot.height;
                Fit(ref w, ref h, MaxRT);
                sh.AlbedoRT = MakeRT("SfMorph marks albedo " + sh.Name, w, h, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, true, aSlot);
            }
            if (ownNormal)
            {
                int w = nBase.width, h = nBase.height;
                Fit(ref w, ref h, MaxRT);
                sh.NormalRT = MakeRT("SfMorph marks normal " + sh.Name, w, h, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, true, nBase);
            }
            sh.LastNormalFor = nf;
            RasterSheet(sh);                                   // the copies start with the current marks, before they go into the slots
            if (sh.AlbedoRT != null) { mat.SetTexture(sh.AlbedoProp, sh.AlbedoRT); sh.AlbedoHeld = true; }
            if (sh.NormalRT != null) mat.SetTexture(sh.NormalProp, sh.NormalRT);
            sh.Built = true;
            sh.Dirty = true;
            if (sh.ViaWrinkles || sh.AlbedoViaWrinkles) Wrinkles.Touch(mat);

            var names = new StringBuilder();
            int v = 0;
            for (int i = 0; i < sh.Rends.Count; i++)
            {
                if (i > 0) names.Append(", ");
                names.Append(sh.Rends[i].Name);
                if (sh.Rends[i].Bake != null) v += sh.Rends[i].Bake.V;
            }
            Plugin.Log.LogInfo("Skin marks: sheet '" + sh.Name + "' (" + mat.shader.name + "): renderers [" + names + "], " + v + " verts, marks RT " + sh.MarksRT.width + "x" + sh.MarksRT.height
                + ", albedo " + (wa ? "via Wrinkles" : sh.AlbedoProp != null ? sh.AlbedoProp + " '" + Describe(aSlot) + "' -> RT " + sh.AlbedoRT.width + "x" + sh.AlbedoRT.height : "slot not found (normal only)")
                + ", normal " + (sh.NormalProp == null ? "none" : wr ? sh.NormalProp + " via Wrinkles" : ownNormal ? sh.NormalProp + " '" + Describe(nBase) + "' -> RT " + sh.NormalRT.width + "x" + sh.NormalRT.height : sh.NormalProp + " has no texture")
                + ", " + sh.Mpu.ToString("0.00") + " m per UV");
        }

        // The texture in the material's albedo slot whoever owns it (size of the marks RT while the albedo is Wrinkles').
        static Texture AlbedoSlotTexture(Material mat)
        {
            for (int i = 0; i < AlbedoProps.Length; i++)
            {
                if (!mat.HasProperty(AlbedoProps[i])) continue;
                var t = mat.GetTexture(AlbedoProps[i]);
                if (t != null && t.width >= 8 && t.height >= 8) return t;
            }
            return null;
        }

        static string Describe(Texture t)
        {
            return t == null ? "none" : (string.IsNullOrEmpty(t.name) ? "<unnamed>" : t.name) + " " + t.width + "x" + t.height;
        }

        void TakeAlbedo(Sheet sh)
        {
            sh.Mat.SetTexture(sh.AlbedoProp, sh.AlbedoRT);
            sh.AlbedoHeld = true;
            sh.Dirty = true;
        }

        // Every 4th frame while a sheet is up: base tracking (the game, Morph and Wrinkles move textures under us), like Wrinkles.ServiceSheets.
        void ServiceSheet(Sheet sh, float now)
        {
            var mat = sh.Mat;
            // ---- albedo ----
            if (Wrinkles.OwnsAlbedoSlot(mat))
            {
                // 0.8.4: Wrinkles keeps its albedo copy (it took our base through ExternalBase): the marks go in through its albedo pass
                if (sh.AlbedoRT != null)
                {
                    if (sh.AlbedoProp != null && mat.GetTexture(sh.AlbedoProp) == sh.AlbedoRT) mat.SetTexture(sh.AlbedoProp, Usable(sh.AlbedoBase));
                    DestroyRT(ref sh.AlbedoRT);
                }
                sh.AlbedoHeld = false;
                sh.AlbedoProp = null;      // found again (and a copy made) when Wrinkles lets go
                if (!sh.AlbedoViaWrinkles) { sh.AlbedoViaWrinkles = true; sh.Dirty = true; Plugin.Log.LogInfo("Skin marks: sheet '" + sh.Name + "': albedo now through Wrinkles"); }
            }
            else if (sh.AlbedoProp == null)
            {
                if (sh.AlbedoViaWrinkles) { sh.AlbedoViaWrinkles = false; sh.NextAlbedoTry = 0f; Plugin.Log.LogInfo("Skin marks: sheet '" + sh.Name + "': Wrinkles let go of the albedo slot"); }
                if (now >= sh.NextAlbedoTry)
                {
                    sh.NextAlbedoTry = now + 1f;
                    string held;
                    string p = FindAlbedo(mat, out held);
                    if (p != null)
                    {
                        var slot = mat.GetTexture(p);
                        sh.AlbedoProp = p; sh.AlbedoBase = slot; sh.AlbedoOrig = OrigOf(mat, p) ?? slot;
                        int w = slot.width, h = slot.height;
                        Fit(ref w, ref h, MaxRT);
                        DestroyRT(ref sh.AlbedoRT);
                        sh.AlbedoRT = MakeRT("SfMorph marks albedo " + sh.Name, w, h, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, true, slot);
                        sh.LastNormalFor = NormalForOf(mat);
                        RasterSheet(sh);                                 // fill it before it goes into the slot
                        TakeAlbedo(sh);
                        Plugin.Log.LogInfo("Skin marks: sheet '" + sh.Name + "': albedo copy on " + p + " (" + Describe(slot) + ")");
                    }
                }
            }
            else
            {
                Texture slot = mat.GetTexture(sh.AlbedoProp);
                Texture nfNow = NormalForOf(mat);
                if (slot == sh.AlbedoRT)
                {
                    // the Characters tab switched the skin textures on or off: Morph skips a slot that holds our texture, so give the base back
                    // for its next 2 s pass and take over whatever it writes (the retake below)
                    if (sh.AlbedoHeld && nfNow != sh.LastNormalFor)
                    {
                        sh.LastNormalFor = nfNow;
                        mat.SetTexture(sh.AlbedoProp, Usable(sh.AlbedoBase));
                        sh.AlbedoHeld = false;
                        sh.RetakeAt = now + 2.4f;
                        Plugin.Log.LogInfo("Skin marks: sheet '" + sh.Name + "': skin textures changed; albedo slot handed back for Morph's next pass");
                    }
                }
                else if (slot != null && !Wrinkles.Owns(slot))
                {
                    if (!sh.AlbedoHeld && slot == sh.AlbedoBase && now < sh.RetakeAt) { /* waiting for Morph to apply the character's albedo */ }
                    else
                    {
                        if (slot != sh.AlbedoBase)
                            Plugin.Log.LogInfo("Skin marks: sheet '" + sh.Name + "': albedo base now " + Describe(slot));
                        sh.AlbedoBase = slot;
                        sh.AlbedoOrig = OrigOf(mat, sh.AlbedoProp) ?? slot;
                        sh.LastNormalFor = nfNow;
                        TakeAlbedo(sh);
                    }
                }
                else if (slot != null) sh.AlbedoHeld = false;   // a render texture of another system (Wrinkles' debug view with a pre-0.8.4 shader bundle) is in it: leave it
                else TakeAlbedo(sh);
            }

            // ---- normal ----
            if (sh.NormalProp == null) return;
            if (Wrinkles.OwnsNormalSlot(mat))
            {
                if (sh.NormalRT != null)
                {
                    // Wrinkles built its sheet (it took our base through ExternalBase): the slot is its, free our copy
                    var cur = mat.GetTexture(sh.NormalProp);
                    if (cur == sh.NormalRT) mat.SetTexture(sh.NormalProp, Usable(sh.NormalBase));
                    DestroyRT(ref sh.NormalRT);
                }
                if (!sh.ViaWrinkles) { sh.ViaWrinkles = true; sh.Dirty = true; Plugin.Log.LogInfo("Skin marks: sheet '" + sh.Name + "': normal now through Wrinkles"); }
                return;
            }
            Texture nSlot = mat.GetTexture(sh.NormalProp);
            Texture nf = NormalForOf(mat);
            Texture want = sh.NormalBase;
            if (nf != null && !Wrinkles.Owns(nf)) want = nf;
            else if (nSlot != null && !Wrinkles.Owns(nSlot)) want = nSlot;
            if (want == null) { sh.ViaWrinkles = false; return; }
            if (want != sh.NormalBase)
            {
                sh.NormalBase = want;
                sh.NormalOrig = OrigOf(mat, sh.NormalProp) ?? want;
                sh.Dirty = true;
                Plugin.Log.LogInfo("Skin marks: sheet '" + sh.Name + "': normal base now " + Describe(want));
            }
            if (sh.NormalRT == null)
            {
                int w = want.width, h = want.height;
                Fit(ref w, ref h, MaxRT);
                sh.NormalRT = MakeRT("SfMorph marks normal " + sh.Name, w, h, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, true, want);
                sh.Dirty = true;
                Plugin.Log.LogInfo("Skin marks: sheet '" + sh.Name + "': " + (sh.ViaWrinkles ? "Wrinkles let go of the normal slot; normal copy " : "normal copy ") + sh.NormalRT.width + "x" + sh.NormalRT.height);
                RasterSheet(sh);                                 // fill it before it goes into the slot
            }
            sh.ViaWrinkles = false;
            if (nSlot != sh.NormalRT) mat.SetTexture(sh.NormalProp, sh.NormalRT);   // self-heal: the game or Morph put its own back
        }

        // Puts the original textures back and frees the sheet's render textures.
        void RestoreSheet(Sheet sh)
        {
            try
            {
                var mat = sh.Mat;
                if (mat != null)
                {
                    if (sh.AlbedoProp != null && sh.AlbedoRT != null && mat.GetTexture(sh.AlbedoProp) == sh.AlbedoRT)
                        mat.SetTexture(sh.AlbedoProp, Usable(sh.AlbedoBase) ?? Usable(sh.AlbedoOrig));
                    if (sh.NormalProp != null && sh.NormalRT != null && mat.GetTexture(sh.NormalProp) == sh.NormalRT)
                        mat.SetTexture(sh.NormalProp, Usable(NormalForOf(mat)) ?? Usable(sh.NormalBase) ?? Usable(sh.NormalOrig));
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Skin marks: restoring '" + sh.Name + "' failed: " + e.Message); }
            DestroyRT(ref sh.MarksRT);
            DestroyRT(ref sh.AlbedoRT);
            DestroyRT(ref sh.NormalRT);
            sh.Built = false;
            sh.AlbedoHeld = false;
        }

        // why: logged ("idle", ...); null = quiet
        void ReleaseSheets(string why)
        {
            if (_sheets.Count == 0) { _anyBuilt = false; return; }
            var wrMats = new List<Material>();
            for (int i = 0; i < _sheets.Count; i++)
            {
                var sh = _sheets[i];
                if ((sh.ViaWrinkles || sh.AlbedoViaWrinkles) && sh.Mat != null) wrMats.Add(sh.Mat);
                RestoreSheet(sh);
                for (int k = 0; k < sh.Rends.Count; k++) ReleaseRend(sh.Rends[k]);
                sh.Rends.Clear();
            }
            _sheets.Clear();
            _anyBuilt = false;
            _nextDiscover = 0f;
            _ver = -1;
            for (int i = 0; i < wrMats.Count; i++) Wrinkles.Touch(wrMats[i]);   // Wrinkles blends the normal again without our dent
            if (why != null) Plugin.Log.LogInfo("Skin marks: " + why + ", slots restored");
        }

        // ---- the raster (render loop) ----

        static bool AnyVisible(Sheet sh)
        {
            for (int k = 0; k < sh.Rends.Count; k++)
            {
                var r = sh.Rends[k].R;
                if (r != null && r.isVisible) return true;
            }
            return false;
        }

        void OnAfterImprint(int frame)
        {
            try
            {
                if (_dead || !_anyBuilt || _flipState != 2 || _gpu == null || _cmd == null || _mat == null) return;
                if (_gpu.BakeVersion != _ver) return;          // GpuContact's buffers changed under us: Update rediscovers next frame
                float now = Time.time;
                bool hot = now - _lastActivity < 2.5f;
                bool dirty = false;
                for (int i = 0; i < _sheets.Count; i++) if (_sheets[i].Built && _sheets[i].Dirty) { dirty = true; break; }
                bool due = hot ? frame - _lastRasterFrame >= 2 : now - _lastRasterT >= 0.3f;   // pressed: every 2nd frame; just fading: ~3 per second
                if (!due && !dirty) return;
                bool any = false;
                for (int i = 0; i < _sheets.Count; i++)
                {
                    var sh = _sheets[i];
                    if (!sh.Built || (!sh.Dirty && !AnyVisible(sh))) continue;
                    RasterSheet(sh);
                    any = true;
                }
                if (any) { _lastRasterFrame = frame; _lastRasterT = now; }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Skin marks: raster failed, turned off: " + e);
                GoDead("error");
            }
        }

        // Pass 0 marks raster over the sheet's renderers, pass 1 albedo, pass 2 normal (own only), mips; Wrinkles told when it owns the normal.
        void RasterSheet(Sheet sh)
        {
            var cmd = _cmd;
            cmd.Clear();
            _mat.SetFloat(PFlip, _flip);
            if (!sh.MarksRT.IsCreated()) sh.MarksRT.Create();
            cmd.SetRenderTarget(sh.MarksRT);
            cmd.ClearRenderTarget(false, true, new Color(0f, 0f, 0f, 0f));
            for (int i = 0; i < sh.Rends.Count; i++)
            {
                var rd = sh.Rends[i];
                if (rd.Ok) cmd.DrawProcedural(Matrix4x4.identity, _mat, 0, MeshTopology.Triangles, 3 * rd.TriCount, 1, rd.Mpb);
            }
            var texel = new Vector4(1f / sh.MarksRT.width, 1f / sh.MarksRT.height, 0f, 0f);
            float scale = sh.Mpu > 1e-4f ? 1f / sh.Mpu : 1f;
            if (sh.AlbedoRT != null)
            {
                var m = sh.MpbA;
                m.SetTexture(PBaseAlbedo, sh.AlbedoBase != null ? sh.AlbedoBase : (Texture)Texture2D.whiteTexture);
                m.SetTexture(PMarksTex, sh.MarksRT);
                m.SetVector(PMarksTexel, texel);
                m.SetFloat(PRedness, _set.Redness);
                m.SetFloat(PAo, 0.35f * _set.Ao);
                cmd.SetRenderTarget(sh.AlbedoRT);
                cmd.DrawProcedural(Matrix4x4.identity, _mat, 1, MeshTopology.Triangles, 3, 1, m);
                cmd.GenerateMips(sh.AlbedoRT);
            }
            if (sh.NormalRT != null)
            {
                var m = sh.MpbN;
                m.SetTexture(PBaseNormal, sh.NormalBase != null ? sh.NormalBase : (Texture)Texture2D.blackTexture);
                m.SetTexture(PMarksTex, sh.MarksRT);
                m.SetVector(PMarksTexel, texel);
                m.SetFloat(PMarksScale, scale);
                cmd.SetRenderTarget(sh.NormalRT);
                cmd.DrawProcedural(Matrix4x4.identity, _mat, 2, MeshTopology.Triangles, 3, 1, m);
                cmd.GenerateMips(sh.NormalRT);
            }
            Graphics.ExecuteCommandBuffer(cmd);
            sh.Dirty = false;
            if (++_rasters == 1) Plugin.Log.LogInfo("Skin marks: first raster of sheet '" + sh.Name + "' done");
            if ((sh.ViaWrinkles || sh.AlbedoViaWrinkles) && sh.Mat != null) Wrinkles.Touch(sh.Mat);
        }

        // ---- off / teardown ----

        void GoDead(string why)
        {
            _dead = true;
            _why = why;
            Status = "Skin marks: off: " + why;
            _marksOn = false;
            _discN = 0;
            if (_subscribed) { _subscribed = false; GpuContact.AfterImprint -= OnAfterImprint; }
        }

        // Frees everything that touches Unity objects; the slots get their textures back first. Safe to call twice.
        void Teardown()
        {
            try { ReleaseSheets(null); } catch (Exception e) { Plugin.Log.LogWarning("Skin marks: restore failed: " + e.Message); }
            if (_subscribed) { _subscribed = false; GpuContact.AfterImprint -= OnAfterImprint; }
            if (Wrinkles.ExternalBase == _baseFn) Wrinkles.ExternalBase = null;
            DestroyRT(ref _testA);
            DestroyRT(ref _testB);
            if (_cmd != null) { _cmd.Release(); _cmd = null; }
            if (_mat != null) { UnityEngine.Object.Destroy(_mat); _mat = null; }
            _shader = null;
            _flipState = 0; _flipTries = 0; _flip = -1f;
            _anyBuilt = false;
            ShaderBundleRelease();
            _logged.Clear();
            _rasters = 0;
        }

        public void Release()
        {
            try { Teardown(); } catch (Exception e) { Plugin.Log.LogWarning("Skin marks: release failed: " + e.Message); }
            _marksOn = false;
            _discN = 0;
            _dead = false; _loadFailed = false;
            if (_cur == this) _cur = null;
            Status = "";
        }
    }
}
