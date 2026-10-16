using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.Rendering;

namespace SfMorph
{
    internal class WrinkleSettings
    {
        public bool On, Face = true, Neck = true, BodyOn;
        public float Strength = 1f, Threshold = 0.025f;
        public int Debug;             // 0 off, 1 activation, 2 regions
        public bool Recapture;        // one-shot (panel button)
        public float Attack = 0.12f, Release = 0.3f, CullDistance = 3f;
        public int Resolution;        // 0 base size (cap 4096), else 2048/1024
        public float Cavity = 0.3f;   // 0.8.4: crease shade in the albedo copy (0 = no albedo copy unless the debug view is on)
        // 0.10.0: muscle definition, a second layer of the same sheets (Muscles.cs); On above = the wrinkles themselves
        public readonly MuscleSettings Muscles = new MuscleSettings();
    }

    // PlayerLoop marker type of the wrinkle step (right after PostLateUpdate.UpdateAllSkinnedMeshes).
    internal struct WrinkleStep { }

    // Skin wrinkles (0.9.0, after the procedural folds of 0.7.4-0.8.9): authored crease maps per UV layout (tools/wrinkles/author.py, in the
    // shader bundle: her face sheet, her body atlas) faded in per region, the way games blend expression wrinkle maps. The driver is her
    // skin's compression on the GPU (Wrinkles.compute: per region triangle, the stretch across that region's crease lines against the
    // neutral pose, pooled per region) read back to the CPU, which fades each region in with attack / release; WrinkleRaster.shader blends
    // the faded creases into copies of the skin's normal map (and albedo, for the crease shade). Only the authored regions can wrinkle
    // (face expressions, neck, joint creases); breasts, shoulders and the rest of her skin never do.
    // Update(her, s) runs on the main thread each frame (discovery, CPU bakes, activations, texture assignment, culling); Run(frame) is the
    // GPU part, hooked to GpuContact.BeforeImprint, a PlayerLoop step and beginContextRendering (whichever comes first wins, frame guard).
    // Nothing here may throw into the render loop: any exception logs, switches the system off and leaves the teardown to the next
    // main-thread call.
    internal class Wrinkles
    {
        public string Status = "";
        // Why the last TryLoad returned null (same wording as Status).
        public static string LastStatus = "";
        public bool Dead { get { return _dead; } }

        // ---- our render textures (Morph.cs leaves a slot holding one of these alone) ----

        static readonly HashSet<int> _owned = new HashSet<int>();
        public static bool Owns(Texture t) { return t != null && _owned.Contains(t.GetInstanceID()); }

        // ---- 0.8.0 hooks for SkinMarks (it shares the owned set, the skin slots and the normal blend) ----

        // Other systems' render textures in a skin slot (SkinMarks): Morph and Wrinkles treat them as ours. Disown before destroying.
        internal static void Own(Texture t) { if (t != null) _owned.Add(t.GetInstanceID()); }
        internal static void Disown(Texture t) { if (t != null) _owned.Remove(t.GetInstanceID()); }

        // What another system (SkinMarks) has under its own render texture in a material's slot: the base to start from when the slot
        // holds a texture we don't know. Set by SkinMarks; null result = unknown.
        internal static Func<Material, string, Texture> ExternalBase;

        static Texture ExternalBaseOf(Material m, string prop)
        {
            var f = ExternalBase;
            if (f == null || m == null || string.IsNullOrEmpty(prop)) return null;
            try { return Usable(f(m, prop)); }
            catch { return null; }
        }

        // SkinMarks' marks changed: the sheet of this material blends the normal again (its pass 1 adds the marks' dent).
        internal static void Touch(Material m)
        {
            var w = _live;
            if (w == null || w._dead || m == null) return;
            var sheets = w._sheets;
            for (int i = 0; i < sheets.Count; i++)
            {
                var sh = sheets[i];
                if (sh == null || sh.Mat != m || !sh.Built) continue;
                sh.Dirty = true;
                sh.LastChange = Time.unscaledTime;
            }
        }

        // True while a built Wrinkles sheet has its normal RT in this material's normal slot (SkinMarks then leaves that slot to it).
        internal static bool OwnsNormalSlot(Material m)
        {
            var w = _live;
            if (w == null || w._dead || !w._active || m == null) return false;
            var sheets = w._sheets;
            for (int i = 0; i < sheets.Count; i++)
            {
                var sh = sheets[i];
                if (sh != null && sh.Mat == m && sh.Built && !sh.Skip && sh.Normal != null) return true;
            }
            return false;
        }

        // 0.8.4: true while a built Wrinkles sheet keeps its albedo copy (crease darkening / debug view, with the marks composed in) for this
        // material, including the few seconds it hands the slot back for Morph's skin toggle. SkinMarks then leaves the albedo slot to it.
        internal static bool OwnsAlbedoSlot(Material m)
        {
            var w = _live;
            if (w == null || w._dead || !w._active || !w._albedoPass || m == null) return false;
            var sheets = w._sheets;
            for (int i = 0; i < sheets.Count; i++)
            {
                var sh = sheets[i];
                if (sh != null && sh.Mat == m && sh.Built && !sh.Skip && sh.Albedo != null) return true;
            }
            return false;
        }

        // The self-tested clip-Y sign of the live instance (-1 = D3D until it has settled, or when none runs).
        internal static float FlipY { get { var w = _live; return w != null ? w._flip : -1f; } }
        internal static bool FlipSettled { get { var w = _live; return w != null && w._flipState == 2; } }

        // ---- constants ----

        public const int MaxRegions = 32;        // per layout (the shader's _Act array)
        const int MaxInflight = 6;
        const string BsWhy = "blendshape weights changed";
        const float HandBack = 2.4f;             // Morph re-applies the character's textures every 2 s
        const int BlendPass = 0, TestPass = 1, AlbedoPass = 2;
        const float PeakLogEvery = 10f;
        const float BaseRise = 20f;              // 0.9.8: seconds for a face / neck baseline to rise to a held compression
        static readonly string[] NormalProps = { "_NormalMap", "_BumpMap" };
        // Morph.cs's list (the game's skin is a shader graph: the property is not certain)
        internal static readonly string[] AlbedoProps = { "_ColorMap", "_BaseColorMap", "_BaseMap", "_MainTex", "_AlbedoMap", "_Albedo", "_DiffuseMap", "_Diffuse" };

        static readonly int PVerts = Shader.PropertyToID("_Verts"), PStride = Shader.PropertyToID("_Stride"), PPosOff = Shader.PropertyToID("_PosOff"),
            PTriCount = Shader.PropertyToID("_TriCount"), PTris = Shader.PropertyToID("_Tris"),
            PFlip = Shader.PropertyToID("_FlipY"), PStrength = Shader.PropertyToID("_Strength"),
            PTestMode = Shader.PropertyToID("_TestMode"), PBaseNormal = Shader.PropertyToID("_BaseNormal"),
            PBaseAlbedo = Shader.PropertyToID("_BaseAlbedo"), PTest = Shader.PropertyToID("_Test"),
            PDebugMode = Shader.PropertyToID("_DebugMode"), PInvScale2 = Shader.PropertyToID("_InvScale2"),
            PMarks = Shader.PropertyToID("_Marks"), PMarksOn = Shader.PropertyToID("_MarksOn"), PMarksTexel = Shader.PropertyToID("_MarksTexel"),
            PMarksScale = Shader.PropertyToID("_MarksScale"), PCavity = Shader.PropertyToID("_Cavity"),
            PMarksAlbedoOn = Shader.PropertyToID("_MarksAlbedoOn"), PRedness = Shader.PropertyToID("_Redness"), PAo = Shader.PropertyToID("_Ao"),
            PRegTri = Shader.PropertyToID("_RegTri"), PSums = Shader.PropertyToID("_Sums"), PSumCount = Shader.PropertyToID("_SumCount"),
            PDetail = Shader.PropertyToID("_Detail"), PRegion = Shader.PropertyToID("_Region"), PRegionSize = Shader.PropertyToID("_RegionSize"),
            PAct = Shader.PropertyToID("_Act");
        // 0.10.0 muscle layer
        static readonly int PMOn = Shader.PropertyToID("_MOn"), PMDetail = Shader.PropertyToID("_MDetail"), PMRegion = Shader.PropertyToID("_MRegion"),
            PMRegionSize = Shader.PropertyToID("_MRegionSize"), PMAct = Shader.PropertyToID("_MAct"), PMStrength = Shader.PropertyToID("_MStrength"),
            PMShade = Shader.PropertyToID("_MShade"), PMDebug = Shader.PropertyToID("_MDebug"), PMSlopeMax = Shader.PropertyToID("_MSlopeMax");
        const float MuscleStep = 1f / 32f, MuscleMinInterval = 0.1f;

        // ---- state ----

        class Rend
        {
            public SkinnedMeshRenderer R;
            public int Sub, Region;               // Region: 1 face, 2 neck, 3 body
            public string Name;
            public Material Mat;
            public Sheet S;
            public BakeResult B;
            public Mesh Mesh;
            public int V, T;
            public int State;                     // 0 needs a bake, 1 baked and uploaded, -1 failed (retry at RetryAt)
            public float RetryAt;
            public string LastError = "";
            public double BakeMs;
            public bool RawAcquired;
            public bool Alive = true;
            public int Layout;                    // 0 = skinned buffer not checked yet, 1 = ok, -1 = unusable
            public int Stride, WarnedRaw, NoVb;
            public bool NoVbLogged;
            public float ScaleFix = 1f;           // median live/rest vertex distance ratio (1 until measured)
            public bool ScaleChecked;
            public int RT;                        // region triangles (0: nothing of this renderer is under a region)
            public GraphicsBuffer Tris, RegTri;
            public long Bytes;
            public float[] BsSnap;
            public int SoftVer, BindVer;
            public bool NeedRecapture;
            public string RecaptureWhy = "";
            public float LastRecapture, BsSuspendUntil;
            public int BsBurst;
            public float BsBurstStart;
            public bool Ready { get { return Alive && State == 1 && R != null; } }
        }

        class Sheet
        {
            public Material Mat;
            public string Name, Prop, AlbedoProp;
            public bool Face, Skip, Empty;        // Empty: its renderers have no triangle under a region (not built)
            public WrinkleLayout Layout;
            public readonly List<Rend> Rends = new List<Rend>();
            public GraphicsBuffer Sums;           // 2 uint per region (Wrinkles.compute)
            public int Inflight;
            public Action<AsyncGPUReadbackRequest> OnSums;
            public readonly float[] Comp = new float[MaxRegions];     // pooled compression per region (latest readback)
            public readonly float[] Act = new float[MaxRegions];      // faded-in activation per region (CPU, attack / release)
            public readonly float[] ActSent = new float[MaxRegions];  // the activations of the last raster
            public readonly float[] Peak = new float[MaxRegions];     // peak compression since the last peak log
            public readonly float[] Idle = new float[MaxRegions];     // 0.9.8: face / neck idle baseline (slow minimum of the compression)
            public readonly bool[] IdleSet = new bool[MaxRegions];
            public float LastActAt, NextPeakLog;
            public string LastPeakLine = "";
            public Texture Base;                  // the normal map under ours (the game's, or Morph's baked one)
            public Texture AlbedoBase;            // the albedo under our copy (the game's, the character's, or what SkinMarks has under its copy)
            public Texture AlbedoOrig;            // her own albedo (Morph's saved original when a character skin is on)
            public Texture Orig;                  // her own normal map (what to put back when we leave; never Morph's baked one or ours)
            public RenderTexture Normal, Albedo;  // Albedo (0.8.4): crease darkening + SkinMarks' marks + the debug tint, in the albedo slot
            public int DebugMode;
            public bool Built, Dirty, Asleep, Culled, RasterLogged, AlbedoHeld;
            public float LastChange, LastComputeAt, LastTransLog = -100f;
            public int LastRasterFrame = -100;
            public float StrengthApplied = -1f, CavityApplied = -1f;
            public int DebugApplied = -1;
            public int SkinSig;                   // Morph's skin state when last checked (a change while our copy is in the slot = Morph skipped it)
            public Texture Pending;               // a texture someone else put in the albedo slot while a character skin is on: taken once it stays
            public float PendingSince;
            public int Res;
            public MaterialPropertyBlock Mpb = new MaterialPropertyBlock();
            // 0.10.0 muscles: the layer of this sheet's layout (null: none), tension from her pose, faded activation, what the last raster had
            public MuscleLayer Muscle;
            public readonly float[] MTension = new float[MaxRegions], MAct = new float[MaxRegions], MActSent = new float[MaxRegions], MPeak = new float[MaxRegions];
            public float MLastAt, MLastDirty, MStrengthApplied = -1f, MShadeApplied = -1f;
            public int MDebugApplied = -1;
            public bool MOnApplied;
        }

        ComputeShader _cs;
        Shader _shader;
        Material _mat;
        CommandBuffer _cmd;
        int _kClear, _kStrain;
        readonly List<WrinkleLayout> _layouts = new List<WrinkleLayout>();
        readonly List<MuscleLayer> _muscleLayers = new List<MuscleLayer>();
        bool _muscleShader;                       // the bundle's WrinkleRaster has the muscle layer (_MDetail)
        RenderTexture _testA, _testB;
        Texture2D _flat;
        MaterialPropertyBlock _mpbTestA = new MaterialPropertyBlock(), _mpbTestB = new MaterialPropertyBlock();
        float _flip = -1f;                        // D3D default; the self-test confirms or flips it
        int _flipState, _flipTries;               // 0 = not run, 1 = readback pending, 2 = settled
        readonly List<Rend> _rends = new List<Rend>();
        readonly List<Sheet> _sheets = new List<Sheet>();
        readonly HashSet<string> _logged = new HashSet<string>();
        WrinkleSettings _s = new WrinkleSettings();
        Character _her;
        bool _dead, _active, _bundleHeld, _subscribed, _inLoop, _evSeen, _evWarned, _tornDown, _released, _albedoPass;
        int _lastFrame = -1, _waitFrames, _updateCount;
        float _nextDiscover, _recaptureAt, _seenDirtyAt = -1f, _offSince = -1f, _nextCull, _nextStatus;
        bool _recapAll;
        string _recapAllWhy = "";
        bool _farAway;
        Camera _cam;
        float _nextCamFind;
        long _lastMemLog = -1;

        Wrinkles() { }

        // ---- load ----

        public static Wrinkles TryLoad(string dir)
        {
            LastStatus = "Wrinkles: off: not loaded";
            Wrinkles w = null;
            try
            {
                if (!SystemInfo.supportsComputeShaders) { Fail("no compute shaders"); return null; }
                string err;
                if (!ShaderBundle.Acquire(dir, out err)) { Fail("shader bundle (" + err + ")"); return null; }
                w = new Wrinkles();
                w._bundleHeld = true;
                w._cs = ShaderBundle.Load<ComputeShader>("Wrinkles");
                w._shader = ShaderBundle.Load<Shader>("WrinkleRaster");
                if (w._cs == null) { Fail("bundle (no Wrinkles compute shader inside)"); w.Teardown(); return null; }
                if (w._shader == null) { Fail("bundle (no WrinkleRaster shader inside)"); w.Teardown(); return null; }
                // 0.9.0 kernels; a bundle from before 0.9.0 has the procedural folds' kernels (Clear/TriStrain/VertexGather) and no maps
                if (!w._cs.HasKernel("RegionClear") || !w._cs.HasKernel("RegionStrain"))
                { Fail("the shader bundle is older than this DLL (no authored wrinkles inside): install the sfmorph_shaders that came with it"); w.Teardown(); return null; }
                w._kClear = w._cs.FindKernel("RegionClear");
                w._kStrain = w._cs.FindKernel("RegionStrain");
                if (!w._shader.isSupported) { Fail("WrinkleRaster shader not supported on " + SystemInfo.graphicsDeviceType); w.Teardown(); return null; }
                w._mat = new Material(w._shader) { hideFlags = HideFlags.HideAndDontSave };
                w._albedoPass = w._mat.passCount > AlbedoPass;
                if (w._mat.passCount != 3) { Fail("the shader bundle's WrinkleRaster has " + w._mat.passCount + " passes (3 expected): install the sfmorph_shaders that came with this DLL"); w.Teardown(); return null; }
                var table = ShaderBundle.Load<TextAsset>("wrinkle_layouts");
                if (table == null) { Fail("bundle (no wrinkle_layouts inside)"); w.Teardown(); return null; }
                var notes = new List<string>();
                w._layouts.AddRange(WrinkleLayout.Parse(table.text, n => ShaderBundle.Load<Texture2D>(n), notes));
                foreach (var n in notes) Plugin.Log.LogWarning("Wrinkles: layout " + n);
                if (w._layouts.Count == 0) { Fail("bundle (no usable wrinkle layout)"); w.Teardown(); return null; }
                // 0.10.0 muscle layers (optional: an older bundle has none; the muscles then say so and the wrinkles work as before)
                w._muscleShader = w._shader.FindPropertyIndex("_MDetail") >= 0;
                var mtable = ShaderBundle.Load<TextAsset>("muscle_layouts");
                if (mtable != null && w._muscleShader)
                {
                    var mnotes = new List<string>();
                    w._muscleLayers.AddRange(MuscleLayer.Parse(mtable.text, n => ShaderBundle.Load<Texture2D>(n), mnotes));
                    foreach (var n in mnotes) Plugin.Log.LogWarning("Muscles: layout " + n);
                }
                w._cmd = new CommandBuffer { name = "SfMorph wrinkles" };
                w._flat = new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { name = "SfMorph flat normal", hideFlags = HideFlags.HideAndDontSave };
                w._flat.SetPixel(0, 0, new Color(0.5f, 0.5f, 1f, 1f));
                w._flat.Apply(false, false);
                GpuContact.BeforeImprint += w.OnBeforeImprint;
                RenderPipelineManager.beginContextRendering += w.OnBeginContext;
                w._subscribed = true;
                w._inLoop = InsertStep(w.OnLoopStep);
                var ls = new StringBuilder();
                foreach (var L in w._layouts)
                    ls.Append(ls.Length > 0 ? "; " : "").Append(L.Name).Append(" (").Append(L.Count).Append(" regions: ").Append(string.Join(" ", L.RegionName.ToArray()))
                      .Append(", creases ").Append(L.Detail.width).Append('x').Append(L.Detail.height).Append(')');
                var mls = new StringBuilder();
                foreach (var M in w._muscleLayers) mls.Append(mls.Length > 0 ? "; " : "").Append(M.Name).Append(" (").Append(M.Count).Append(" muscles, ").Append(M.Detail.width).Append('x').Append(M.Detail.height).Append(')');
                Plugin.Log.LogInfo("Muscles: " + (mls.Length > 0 ? "layouts " + mls : !w._muscleShader ? "none (the bundle's WrinkleRaster has no muscle layer: install the sfmorph_shaders that came with this DLL)" : "no muscle layout in the bundle"));
                Plugin.Log.LogInfo("Wrinkles: loaded on " + SystemInfo.graphicsDeviceType + ", authored layouts: " + ls
                    + ", PlayerLoop step " + (w._inLoop ? "inserted after UpdateAllSkinnedMeshes" : "not inserted (BeforeImprint + SRP hooks only)"));
                w.Status = "Wrinkles: loaded";
                LastStatus = w.Status;
                _live = w;
                return w;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Wrinkles: load failed: " + e);
                Fail("load error");
                try { if (w != null) w.Teardown(); } catch { }
                return null;
            }
        }

        static Wrinkles _live;

        // The crease shade (albedo pass) is there (always with a 0.9.0 bundle; the load refuses older ones).
        public bool HasCavity { get { return _albedoPass; } }
        // 0.10.0: the bundle carries the muscle layer (maps + shader)
        public bool HasMuscles { get { return _muscleLayers.Count > 0; } }

        // Called by Morph.cs by reflection: the base texture Wrinkles saw under its own texture in this material slot (her own / Morph's
        // normal map for the normal slot, her own albedo for the albedo copy's slot); null when Wrinkles doesn't own the slot or doesn't know.
        // Never throws, never returns a texture we own.
        internal static Texture BaseOf(Material m, string prop)
        {
            try
            {
                if (m == null || string.IsNullOrEmpty(prop)) return null;
                var w = _live;
                if (w != null && !w._dead)
                {
                    var sheets = w._sheets;
                    for (int i = 0; i < sheets.Count; i++)
                    {
                        var sh = sheets[i];
                        if (sh == null || sh.Mat != m) continue;
                        if (sh.Built && sh.Prop == prop) return Usable(sh.Orig) ?? Usable(sh.Base);
                        if (sh.Albedo != null && sh.AlbedoProp == prop) return Usable(sh.AlbedoOrig) ?? Usable(sh.AlbedoBase);
                        break;
                    }
                }
                return ExternalBaseOf(m, prop);   // 0.8.0: the slot may hold SkinMarks' albedo / normal copy instead
            }
            catch { }
            return null;
        }

        static void Fail(string why)
        {
            LastStatus = "Wrinkles: off: " + why;
            Plugin.Log.LogWarning(LastStatus);
        }

        // ---- the per-frame hooks (GPU side) ----

        void OnBeforeImprint(int frame)
        {
            Safe(frame);
        }

        void OnLoopStep()
        {
            Safe(Time.frameCount);
        }

        void OnBeginContext(ScriptableRenderContext ctx, List<Camera> cams)
        {
            if (_dead || !HasGameCamera(cams)) return;
            Safe(Time.frameCount);
        }

        static bool HasGameCamera(List<Camera> cams)
        {
            if (cams == null) return false;
            for (int i = 0; i < cams.Count; i++)
            {
                var c = cams[i];
                if (c != null && c.cameraType == CameraType.Game && (c.stereoEnabled || c.targetTexture == null)) return true;
            }
            return false;
        }

        void Safe(int frame)
        {
            try
            {
                if (_dead || _released) return;
                Perf.Begin(Perf.S.WrinkleRun);
                Run(frame);
                Perf.End(Perf.S.WrinkleRun);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Wrinkles: frame hook failed, turned off: " + e);
                GoDead("error");
            }
        }

        // The GPU work of a frame: per visible sheet, the region sums (clear, one strain dispatch per renderer, a readback), then (when an
        // activation moved, at most every 2nd frame) the blend into the sheet's normal RT. Idempotent per frame.
        void Run(int frame)
        {
            if (frame == _lastFrame) return;
            _lastFrame = frame;
            _evSeen = true;
            if (!_active || _sheets.Count == 0 || _flipState != 2 || _s == null) return;
            float now = Time.unscaledTime;
            for (int si = 0; si < _sheets.Count; si++)
            {
                var sh = _sheets[si];
                if (!sh.Built || sh.Culled || sh.Sums == null) continue;
                if (!sh.Asleep && !sh.Dirty && now - sh.LastChange > 0.5f)
                {
                    sh.Asleep = true;
                    TransLog(sh, now, "Wrinkles: sheet '" + sh.Name + "' idle (no change for 0.5 s): strain every 4th frame");
                }
                // 0.10.0: the strain only drives the wrinkles; with only the muscles on there is nothing to measure
                bool compute = _s.On && (!sh.Asleep || (frame & 3) == 0) && sh.Inflight < MaxInflight;
                if (compute)
                {
                    var cs = _cs;
                    cs.SetInt(PSumCount, 2 * MaxRegions);
                    cs.SetBuffer(_kClear, PSums, sh.Sums);
                    cs.Dispatch(_kClear, 1, 1, 1);
                    int n = 0;
                    for (int i = 0; i < sh.Rends.Count; i++)
                    {
                        var r = sh.Rends[i];
                        if (r.Ready && r.RT > 0 && Dispatch(r, sh)) n++;
                    }
                    if (n > 0)
                    {
                        sh.Inflight++;
                        AsyncGPUReadback.Request(sh.Sums, sh.OnSums);
                    }
                }
                if (sh.Dirty && frame - sh.LastRasterFrame >= 2) Raster(sh, frame, false);
            }
        }

        // One renderer's region triangles into its sheet's sums; false when it couldn't run (hidden, no buffer yet, layout mismatch).
        bool Dispatch(Rend r, Sheet sh)
        {
            var sr = r.R;
            if (sr == null || !sr.enabled || !sr.gameObject.activeInHierarchy) return false;
            if (sr.sharedMesh != r.Mesh) return false;          // Update re-bakes it
            if (r.Layout < 0) return false;
            using (var vb = sr.GetVertexBuffer())
            {
                if (vb == null)
                {
                    if (++r.NoVb == 300 && !r.NoVbLogged)
                    {
                        r.NoVbLogged = true;
                        Plugin.Log.LogWarning("Wrinkles: renderer " + r.Name + ": skinned vertex buffer unavailable (CPU skinning?)");
                    }
                    return false;
                }
                r.NoVb = 0;
                if (r.Layout == 0 && !CheckBuffer(r, vb)) return false;
                if ((vb.target & GraphicsBuffer.Target.Raw) == 0)
                {
                    if (++r.WarnedRaw == 300) Plugin.Log.LogWarning("Wrinkles: renderer " + r.Name + "'s skinned buffer has no Raw target after 300 frames; waiting");
                    return false;
                }
                var cs = _cs;
                cs.SetInt(PStride, r.Stride);
                cs.SetInt(PPosOff, r.B.PosOff);
                cs.SetInt(PTriCount, r.RT);
                float sf = r.ScaleFix;
                cs.SetFloat(PInvScale2, 1f / (sf * sf));
                cs.SetBuffer(_kStrain, PVerts, vb);
                cs.SetBuffer(_kStrain, PTris, r.Tris);
                cs.SetBuffer(_kStrain, PRegTri, r.RegTri);
                cs.SetBuffer(_kStrain, PSums, sh.Sums);
                cs.Dispatch(_kStrain, (r.RT + 63) / 64, 1, 1);
                if (!r.ScaleChecked) { r.ScaleChecked = true; RequestScaleCheck(r, vb); }
            }
            return true;
        }

        // Sanity check of the skinned buffer's scale against the baked rest positions (a rootBone scale would read as uniform strain):
        // median live/rest distance ratio of consecutive vertices among the first 512. Logged once per renderer.
        void RequestScaleCheck(Rend r, GraphicsBuffer vb)
        {
            int n = Math.Min(512, r.V);
            int stride = r.Stride, posOff = r.B.PosOff;
            if (n < 8) return;
            var rest = r.B.RestP;
            string name = r.Name;
            AsyncGPUReadback.Request(vb, n * stride, 0, req =>
            {
                try
                {
                    if (!r.Alive || _dead || req.hasError || rest == null) return;
                    var d = req.GetData<float>();
                    var ratios = new List<float>(n);
                    for (int i = 0; i + 1 < n && 3 * (i + 1) + 2 < rest.Length; i++)
                    {
                        int f0 = (i * stride + posOff) / 4, f1 = ((i + 1) * stride + posOff) / 4;
                        if (f1 + 2 >= d.Length) break;
                        var a = new Vector3(d[f0], d[f0 + 1], d[f0 + 2]);
                        var b = new Vector3(d[f1], d[f1 + 1], d[f1 + 2]);
                        var ra = new Vector3(rest[3 * i], rest[3 * i + 1], rest[3 * i + 2]);
                        var rb = new Vector3(rest[3 * i + 3], rest[3 * i + 4], rest[3 * i + 5]);
                        float dr = (rb - ra).magnitude;
                        if (dr < 1e-4f) continue;
                        ratios.Add((b - a).magnitude / dr);
                    }
                    if (ratios.Count < 8) return;
                    ratios.Sort();
                    float med = ratios[ratios.Count / 2];
                    if (med > 0.01f && med < 100f) r.ScaleFix = med;   // the strain kernel divides C by med^2
                    string msg = "Wrinkles: scale check " + name + ": median live/rest vertex distance " + med.ToString("0.000") + " over " + ratios.Count + " pairs";
                    // ScaleFix divides the strain by med^2, so a moderately scaled skinning space (the breast-size scale on a root bone) is
                    // handled: say so. Far from 1 the ratio itself is doubtful (wrong buffer layout, a stretched mesh): that one warns.
                    if (med >= 0.9f && med <= 1.1f) Plugin.Log.LogInfo(msg);
                    else if (med >= 0.5f && med <= 2f) Plugin.Log.LogInfo(msg + " (scaled skinning space, compensated)");
                    else Plugin.Log.LogWarning(msg + " (expected ~1" + (r.ScaleFix == med ? "; compensated, but check the creases" : "; not compensated: creases will read it as strain") + ")");
                }
                catch (Exception e) { Plugin.Log.LogWarning("Wrinkles: scale check failed: " + e.Message); }
            });
        }

        // The skinned buffer must have one entry per vertex and hold the position (Float32x3) at the baked offset.
        bool CheckBuffer(Rend r, GraphicsBuffer vb)
        {
            string why = null;
            if (vb.count != r.V) why = "buffer count " + vb.count + " != baked vertex count " + r.V;
            else if (r.B.PosOff + 12 > vb.stride) why = "position (offset " + r.B.PosOff + ") does not fit buffer stride " + vb.stride;
            if (why != null)
            {
                r.Layout = -1;
                Plugin.Log.LogWarning("Wrinkles: renderer " + r.Name + " skipped: " + why);
                return false;
            }
            r.Stride = vb.stride;
            r.Layout = 1;
            Plugin.Log.LogInfo("Wrinkles: renderer " + r.Name + " skinned buffer ok: count " + vb.count + " stride " + vb.stride + " posOff " + r.B.PosOff + " target " + vb.target);
            return true;
        }

        // Region sums readback (1-3 frames late): pooled compression per region = sum(weight x compression) / sum(weight).
        void SumsDone(Sheet sh, AsyncGPUReadbackRequest req)
        {
            try
            {
                if (sh.Inflight > 0) sh.Inflight--;
                if (_dead || sh.Sums == null || req.hasError || sh.Layout == null) return;
                var d = req.GetData<uint>();
                int n = Math.Min(sh.Layout.Count, d.Length / 2);
                for (int k = 0; k < n; k++)
                {
                    uint den = d[2 * k + 1];
                    float c = den > 0 ? (float)((double)d[2 * k] / den) : 0f;
                    sh.Comp[k] = c;
                    if (c > sh.Peak[k]) sh.Peak[k] = c;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Wrinkles: readback callback failed, turned off: " + e);
                GoDead("error");
            }
        }

        // Main thread, every frame: each region's activation follows its compression (past the threshold, over the region's range) with
        // attack / release; a region whose part is switched off (Face / Neck / Body) fades out. A visible change (1/64) asks for a blend.
        void UpdateActs(Sheet sh, WrinkleSettings s, float now)
        {
            var L = sh.Layout;
            if (L == null) return;
            float dt = sh.LastActAt > 0f ? Mathf.Clamp(now - sh.LastActAt, 0f, 0.1f) : 0f;
            sh.LastActAt = now;
            float ka = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, s.Attack)), kr = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, s.Release));
            bool changed = false;
            for (int k = 0; k < L.Count; k++)
            {
                string cls = L.RegionClass[k];
                bool on = s.On && (cls == "face" ? s.Face : cls == "neck" ? s.Neck : s.BodyOn);
                // 0.9.8: face and neck measure from their idle baseline: her idle face animation keeps the neck at ~0.03 and the brows at
                // 0.04-0.06, which held those lines on (headset: a ring around her neck at 72 %). The baseline drops at once to a lower
                // compression and rises over BaseRise s, so an expression shows and a held one fades; the body creases stay absolute
                // (a bent elbow keeps its crease).
                float c = sh.Comp[k];
                if (cls != "body")
                {
                    if (!sh.IdleSet[k]) { sh.Idle[k] = c; sh.IdleSet[k] = true; }
                    float b = sh.Idle[k];
                    b = c < b ? c : b + (c - b) * Mathf.Min(1f, dt / BaseRise);
                    sh.Idle[k] = b;
                    c = Mathf.Max(0f, c - b);
                }
                float x = on ? Mathf.Clamp01((c - s.Threshold) / L.Range[k]) : 0f;
                float target = x * x * (3f - 2f * x);
                float a = sh.Act[k];
                a += (target - a) * (target > a ? ka : kr);
                if (target <= 0.004f && a < 0.004f) a = 0f;
                sh.Act[k] = a;
                if (Mathf.Floor(a * 64f) != Mathf.Floor(sh.ActSent[k] * 64f) || (a == 0f) != (sh.ActSent[k] == 0f)) changed = true;
            }
            if (changed)
            {
                sh.Dirty = true;
                sh.LastChange = now;
                if (sh.Asleep)
                {
                    sh.Asleep = false;
                    TransLog(sh, now, "Wrinkles: sheet '" + sh.Name + "' awake (a region's activation moved)");
                }
            }
            if (now >= sh.NextPeakLog)
            {
                sh.NextPeakLog = now + PeakLogEvery;
                var line = PeakLine(sh, s);
                for (int k = 0; k < MaxRegions; k++) sh.Peak[k] = 0f;
                if (line.Length > 0 && line != sh.LastPeakLine) Plugin.Log.LogInfo("Wrinkles: sheet '" + sh.Name + "' peak compression over " + PeakLogEvery.ToString("0") + " s (threshold " + s.Threshold.ToString("0.000") + "): " + line);
                sh.LastPeakLine = line;
            }
        }

        // The regions that compressed past half the threshold since the last log, strongest first: "name comp (activation now)".
        static string PeakLine(Sheet sh, WrinkleSettings s)
        {
            var L = sh.Layout;
            var idx = new List<int>();
            for (int k = 0; k < L.Count; k++) if (sh.Peak[k] > s.Threshold * 0.5f) idx.Add(k);
            idx.Sort((a, b) => sh.Peak[b].CompareTo(sh.Peak[a]));
            var sb = new StringBuilder();
            for (int i = 0; i < idx.Count && i < 8; i++)
            {
                int k = idx[i];
                sb.Append(sb.Length > 0 ? ", " : "").Append(L.RegionName[k]).Append(' ').Append(sh.Peak[k].ToString("0.000"))
                  .Append(" (").Append((sh.Act[k] * 100f).ToString("0")).Append("%")
                  .Append(L.RegionClass[k] != "body" ? ", idle " + sh.Idle[k].ToString("0.000") : "").Append(")");
            }
            return sb.ToString();
        }

        // The regions active now, for the panel: "forehead_L 45%, elbow_in_R 20%" (strongest first), "" when none.
        public string ActiveRegions()
        {
            var parts = new List<KeyValuePair<float, string>>();
            for (int i = 0; i < _sheets.Count; i++)
            {
                var sh = _sheets[i];
                if (!sh.Built || sh.Layout == null) continue;
                for (int k = 0; k < sh.Layout.Count; k++)
                    if (sh.Act[k] >= 0.05f) parts.Add(new KeyValuePair<float, string>(sh.Act[k], sh.Layout.RegionName[k] + " " + (sh.Act[k] * 100f).ToString("0") + "%"));
            }
            parts.Sort((a, b) => b.Key.CompareTo(a.Key));
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Count && i < 6; i++) sb.Append(i > 0 ? ", " : "").Append(parts[i].Value);
            return sb.ToString();
        }

        void TransLog(Sheet sh, float now, string msg)
        {
            if (now - sh.LastTransLog < 5f) return;
            sh.LastTransLog = now;
            Plugin.Log.LogInfo(msg);
        }

        // ---- blend ----

        // The sheet's normal RT = base normal + the layout's creases faded by the region activations (+ SkinMarks' dent); the albedo copy
        // when it has one. initial: before the first activation (an exact copy of the base: every region at 0).
        void Raster(Sheet sh, int frame, bool initial)
        {
            var cmd = _cmd;
            cmd.Clear();
            _mat.SetFloat(PFlip, _flip);
            _mat.SetFloat(PStrength, _s.Strength);
            var L = sh.Layout;
            for (int k = 0; k < MaxRegions; k++) sh.ActSent[k] = initial || L == null || k >= L.Count ? 0f : sh.Act[k];
            sh.Mpb.SetFloatArray(PAct, sh.ActSent);
            sh.Mpb.SetTexture(PDetail, L != null ? (Texture)L.Detail : Texture2D.blackTexture);
            sh.Mpb.SetTexture(PRegion, L != null ? (Texture)L.Region : Texture2D.blackTexture);
            sh.Mpb.SetVector(PRegionSize, L != null ? new Vector4(L.W, L.H, 0f, 0f) : new Vector4(1f, 1f, 0f, 0f));
            sh.Mpb.SetTexture(PBaseNormal, sh.Base != null ? sh.Base : (Texture)_flat);
            // 0.8.0: SkinMarks' lingering dent of this material (marks RT + metres per UV), a second whiteout in the blend; off when it has none
            RenderTexture mrt = null;
            float msc = 0f;
            try { mrt = SkinMarks.MarksOf(sh.Mat); msc = mrt != null ? SkinMarks.SlopeScaleOf(sh.Mat) : 0f; }
            catch (Exception e) { if (_logged.Add("marksof")) Plugin.Log.LogWarning("Wrinkles: skin marks lookup failed (" + e.Message + "); wrinkles without marks"); mrt = null; msc = 0f; }
            bool mon = mrt != null && msc > 0f;
            sh.Mpb.SetTexture(PMarks, mon ? (Texture)mrt : Texture2D.blackTexture);
            sh.Mpb.SetFloat(PMarksOn, mon ? 1f : 0f);
            sh.Mpb.SetFloat(PMarksScale, mon ? msc : 1f);
            sh.Mpb.SetVector(PMarksTexel, mon ? new Vector4(1f / mrt.width, 1f / mrt.height, 0f, 0f) : new Vector4(0.000488f, 0.000488f, 0f, 0f));
            // 0.10.0: the muscle layer of this sheet (off: _MOn 0, the shader skips it)
            var M = sh.Muscle;
            var ms = _s.Muscles;
            bool mOn = M != null && ms.On;
            for (int k = 0; k < MaxRegions; k++) sh.MActSent[k] = mOn && !initial && k < M.Count ? sh.MAct[k] : 0f;
            sh.Mpb.SetFloat(PMOn, mOn ? 1f : 0f);
            sh.Mpb.SetFloatArray(PMAct, sh.MActSent);
            sh.Mpb.SetTexture(PMDetail, mOn ? (Texture)M.Detail : Texture2D.blackTexture);
            sh.Mpb.SetTexture(PMRegion, mOn ? (Texture)M.Region : Texture2D.blackTexture);
            sh.Mpb.SetVector(PMRegionSize, mOn ? new Vector4(M.Region.width, M.Region.height, 0f, 0f) : new Vector4(1f, 1f, 0f, 0f));
            sh.Mpb.SetFloat(PMSlopeMax, mOn ? M.SlopeMax : 0.6f);
            sh.Mpb.SetFloat(PMStrength, mOn ? ms.Strength : 0f);
            sh.Mpb.SetFloat(PMShade, mOn ? Mathf.Clamp01(ms.Shade) : 0f);
            sh.Mpb.SetFloat(PMDebug, mOn ? ms.Debug : 0f);
            sh.MOnApplied = mOn;
            sh.MStrengthApplied = ms.Strength;
            sh.MShadeApplied = ms.Shade;
            sh.MDebugApplied = ms.Debug;
            cmd.SetRenderTarget(sh.Normal);
            cmd.DrawProcedural(Matrix4x4.identity, _mat, BlendPass, MeshTopology.Triangles, 3, 1, sh.Mpb);
            cmd.GenerateMips(sh.Normal);
            if (sh.Albedo != null)
            {
                sh.Mpb.SetTexture(PBaseAlbedo, sh.AlbedoBase != null ? sh.AlbedoBase : (Texture)Texture2D.whiteTexture);
                _mat.SetFloat(PDebugMode, sh.DebugMode);
                sh.Mpb.SetFloat(PCavity, Mathf.Clamp01(_s.Cavity));
                // SkinMarks' albedo marks on this copy (it leaves the slot to us while we hold it)
                float red = 0f, ao = 0f;
                bool monA = mrt != null && SkinMarks.AlbedoParams(out red, out ao);
                sh.Mpb.SetFloat(PMarksAlbedoOn, monA ? 1f : 0f);
                sh.Mpb.SetFloat(PRedness, red);
                sh.Mpb.SetFloat(PAo, ao);
                if (monA && !mon) { sh.Mpb.SetTexture(PMarks, mrt); sh.Mpb.SetVector(PMarksTexel, new Vector4(1f / mrt.width, 1f / mrt.height, 0f, 0f)); }
                cmd.SetRenderTarget(sh.Albedo);
                cmd.DrawProcedural(Matrix4x4.identity, _mat, AlbedoPass, MeshTopology.Triangles, 3, 1, sh.Mpb);
                cmd.GenerateMips(sh.Albedo);
            }
            Stopwatch sw = null;
            if (!sh.RasterLogged && !initial) sw = Stopwatch.StartNew();
            Graphics.ExecuteCommandBuffer(cmd);
            sh.Dirty = false;
            sh.LastRasterFrame = frame;
            sh.StrengthApplied = _s.Strength;
            sh.CavityApplied = _s.Cavity;
            sh.DebugApplied = sh.DebugMode;
            if (sw != null)
            {
                sw.Stop();
                sh.RasterLogged = true;
                Plugin.Log.LogInfo("Wrinkles: first blend of sheet '" + sh.Name + "' (" + sh.Rends.Count + " renderers, submit " + sw.Elapsed.TotalMilliseconds.ToString("0.00") + " ms CPU)");
            }
        }

        // ---- _FlipY self-test ----

        // A UV gradient is rendered through the same uv -> clip mapping the raster uses, then sampled back at v = 0.25 into a 1x1
        // target; the texel is read back (orientation-proof). 0.25 = mapping consistent with texture sampling, 0.75 = flipped.
        void EnsureFlipTest()
        {
            if (_flipState != 0 || _mat == null) return;
            if (_testA == null)
            {
                _testA = new RenderTexture(16, 16, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "SfMorph wrinkle test A", filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                _testB = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "SfMorph wrinkle test B", filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
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
            cmd.DrawProcedural(Matrix4x4.identity, _mat, TestPass, MeshTopology.Triangles, 3, 1, _mpbTestA);
            cmd.SetRenderTarget(_testB);
            cmd.DrawProcedural(Matrix4x4.identity, _mat, TestPass, MeshTopology.Triangles, 3, 1, _mpbTestB);
            Graphics.ExecuteCommandBuffer(cmd);
            _flipState = 1;
            float used = _flip;
            AsyncGPUReadback.Request(_testB, 0, TextureFormat.RGBA32, req => FlipDone(req, used));
        }

        void FlipDone(AsyncGPUReadbackRequest req, float used)
        {
            try
            {
                if (_released || _dead) return;
                if (req.hasError) { Plugin.Log.LogWarning("Wrinkles: _FlipY self-test readback failed; keeping _FlipY=" + _flip); _flipState = 2; return; }
                var d = req.GetData<byte>();
                float g = d.Length >= 2 ? d[1] / 255f : -1f;
                string verdict;
                if (Mathf.Abs(g - 0.25f) < 0.12f) { verdict = "consistent"; _flipState = 2; }
                else if (Mathf.Abs(g - 0.75f) < 0.12f && _flipTries < 3) { verdict = "flipped, switching"; _flip = -used; _flipState = 0; }
                else { verdict = "inconclusive, keeping"; _flipState = 2; }
                Plugin.Log.LogInfo("Wrinkles: _FlipY self-test: UV gradient probed at v=0.25 read " + g.ToString("0.00") + " with _FlipY=" + used.ToString("0")
                    + " -> " + verdict + (_flipState == 2 ? " (_FlipY=" + _flip.ToString("0") + ")" : ""));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Wrinkles: self-test callback failed, turned off: " + e);
                GoDead("error");
            }
        }

        // ---- Update (main thread) ----

        public void Update(Character her, WrinkleSettings s)
        {
            try
            {
                if (_released) return;
                if (_dead) { if (!_tornDown) Teardown(); return; }
                float now = Time.unscaledTime;
                _updateCount++;
                bool want = s != null && her != null && ((s.On && (s.Face || s.Neck || s.BodyOn)) || (s.Muscles.On && _muscleLayers.Count > 0));
                if (!want)
                {
                    if (_active) GoIdle(now);
                    else if (_offSince > 0f && now - _offSince > 10f && _rends.Count > 0) { DropAll(); FreeShared(); }
                    if (!_dead) Status = "";
                    return;
                }
                _s = s;
                if (!_active)
                {
                    _active = true;
                    _offSince = -1f;
                    Plugin.Log.LogInfo("Wrinkles: on, authored creases " + (s.On ? "on" : "off") + ", muscles " + (s.Muscles.On ? "on (fitness " + s.Muscles.Fitness.ToString("0.00") + ", tension gain " + s.Muscles.Gain.ToString("0.00")
                        + ", strength " + s.Muscles.Strength.ToString("0.00") + ", shade " + s.Muscles.Shade.ToString("0.00") + ")" : "off") + " (face " + s.Face + ", neck " + s.Neck + ", body " + s.BodyOn + ", strength " + s.Strength.ToString("0.00") + ", threshold " + s.Threshold.ToString("0.000")
                        + ", attack " + s.Attack.ToString("0.00") + " s, release " + s.Release.ToString("0.00") + " s, crease shade " + s.Cavity.ToString("0.00")
                        + ", resolution " + (s.Resolution == 0 ? "base (cap 4096)" : s.Resolution.ToString()) + ", cull " + s.CullDistance.ToString("0.0") + " m)");
                }
                if (_her != null && _her.Root != her.Root) { Plugin.Log.LogInfo("Wrinkles: character changed; rebuilding"); DropAll(); }
                if (_her != her)
                {
                    _her = her;
                    _seenDirtyAt = her.DirtyAt;
                    _recaptureAt = 0f;
                }
                if (!_evSeen && !_evWarned && _rends.Count > 0 && ++_waitFrames > 600)
                {
                    _evWarned = true;
                    Plugin.Log.LogWarning("Wrinkles: no frame hook has fired in 600 frames with renderers baked; nothing will update");
                }
                EnsureFlipTest();
                Discover(her, s, now);
                ServiceRends(her, s, now);
                ServiceSheets(her, s, now);
                UpdateCull(her, now);
                if (now >= _nextStatus) { _nextStatus = now + 0.5f; UpdateStatus(); }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Wrinkles: update failed, turned off: " + e);
                GoDead("error");
            }
        }

        // Region of a renderer's material slot (0 = not skin). Material name decides skin or not; the neck comes from the renderer / mesh name.
        // "body" as a word part not followed by a letter (so "bodysuit" / "bodystocking" are clothing; "Body", "body_01", "Body (Instance)" are skin)
        static readonly System.Text.RegularExpressions.Regex BodyRx = new System.Text.RegularExpressions.Regex("body(?![a-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static readonly System.Text.RegularExpressions.Regex ClothRx = new System.Text.RegularExpressions.Regex("suit|stocking|cloth|dress|shirt|skirt|panties|bra", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        internal static int Classify(Material mat, SkinnedMeshRenderer r)
        {
            string ml = (mat.name ?? "").ToLowerInvariant();
            string rl = (r.name ?? "").ToLowerInvariant();
            string sl = r.sharedMesh != null && r.sharedMesh.name != null ? r.sharedMesh.name.ToLowerInvariant() : "";
            if (ml.Contains("face")) return 1;
            bool neckName = rl.Contains("neck") || sl.Contains("neck");
            if (ml.Contains("neck")) return 2;
            bool clothing = ClothRx.IsMatch(ml);
            if (!clothing && (BodyRx.IsMatch(ml) || ml.Contains("foot") || ml.Contains("skin"))) return neckName ? 2 : 3;
            if (ml.Length == 0 || ml.StartsWith("material") || ml.StartsWith("default"))   // anonymous material: go by the mesh
            {
                if (rl.Contains("face") || sl.Contains("face")) return 1;
                if (neckName) return 2;
                if (rl.Contains("body") || sl.Contains("body") || rl.Contains("foot") || sl.Contains("foot")) return 3;
            }
            return 0;
        }

        static bool Wanted(int region, WrinkleSettings s)
        {
            // 0.9.8: the front of her neck is on the body atlas (the body layout's neck region): her body meshes are wanted for Body or
            // Neck; each region class is gated on its own in UpdateActs
            // 0.10.0: and for the muscles (body atlas only)
            bool w = s.On;
            return region == 1 ? w && s.Face : region == 2 ? w && s.Neck : region == 3 && ((w && (s.BodyOn || s.Neck)) || s.Muscles.On);
        }

        Rend FindRend(SkinnedMeshRenderer r, int sub)
        {
            for (int i = 0; i < _rends.Count; i++) if (_rends[i].R == r && _rends[i].Sub == sub) return _rends[i];
            return null;
        }

        Sheet GetSheet(Material mat, bool face)
        {
            for (int i = 0; i < _sheets.Count; i++) if (_sheets[i].Mat == mat) return _sheets[i];
            var sh = new Sheet { Mat = mat, Name = mat.name.Replace(" (Instance)", "").Trim(), Face = face };
            var cap = sh;
            sh.OnSums = req => SumsDone(cap, req);
            sh.Layout = LayoutFor(face);
            sh.Muscle = MuscleFor(face);
            foreach (var p in NormalProps) if (mat.HasProperty(p)) { sh.Prop = p; break; }
            // the albedo: the first of the usual names holding a real texture (ours or SkinMarks' copy count), else the first that exists
            foreach (var p in AlbedoProps)
            {
                if (!mat.HasProperty(p)) continue;
                if (sh.AlbedoProp == null) sh.AlbedoProp = p;
                var t = mat.GetTexture(p);
                if (t != null && t.width >= 8 && t.height >= 8) { sh.AlbedoProp = p; break; }
            }
            if (sh.Prop == null)
            {
                sh.Skip = true;
                if (_logged.Add("noprop|" + sh.Name)) Plugin.Log.LogWarning("Wrinkles: material '" + sh.Name + "' (" + mat.shader.name + ") has no normal map property; skipped");
            }
            else if (sh.Layout == null)
            {
                sh.Skip = true;
                if (_logged.Add("nolayout|" + sh.Name)) Plugin.Log.LogWarning("Wrinkles: material '" + sh.Name + "': no authored " + (face ? "face" : "body") + " wrinkle layout in the bundle; skipped");
            }
            _sheets.Add(sh);
            return sh;
        }

        // The authored layout of a face sheet ("face") or any other skin sheet ("body"); whether a renderer's UVs really are on it is
        // checked per renderer by the bake (coverage).
        WrinkleLayout LayoutFor(bool face)
        {
            string want = face ? "face" : "body";
            for (int i = 0; i < _layouts.Count; i++) if (_layouts[i].Name == want) return _layouts[i];
            return null;
        }

        // 0.10.0: the muscle layer of a sheet's layout (the face has none)
        MuscleLayer MuscleFor(bool face)
        {
            string want = face ? "face" : "body";
            for (int i = 0; i < _muscleLayers.Count; i++) if (_muscleLayers[i].Name == want) return _muscleLayers[i];
            return null;
        }

        // Which renderer slots feed which sheet; rebuilt about once a second and whenever settings change what is wanted.
        void Discover(Character her, WrinkleSettings s, float now)
        {
            int key = (s.Face ? 1 : 0) | (s.Neck ? 2 : 0) | (s.BodyOn ? 4 : 0) | (s.On ? 8 : 0) | (s.Muscles.On ? 16 : 0) | (her.Renderers.Count << 5);
            if (now < _nextDiscover && key == _lastDiscoverKey) return;
            _nextDiscover = now + 1f;
            _lastDiscoverKey = key;
            var keep = new List<Rend>();
            foreach (var r in her.Renderers)
            {
                if (r == null || r.sharedMesh == null) continue;
                var mats = r.sharedMaterials;
                for (int sub = 0; sub < mats.Length && sub < r.sharedMesh.subMeshCount; sub++)
                {
                    var mat = mats[sub];
                    if (mat == null) continue;
                    int region = Classify(mat, r);
                    string ck = "cls|" + r.GetInstanceID() + "|" + sub + "|" + mat.GetInstanceID();
                    if (_logged.Add(ck))
                        Plugin.Log.LogInfo("Wrinkles: renderer '" + r.name + "' mesh '" + r.sharedMesh.name + "' slot " + sub + " material '" + mat.name + "' -> "
                            + (region == 1 ? "face" : region == 2 ? "neck" : region == 3 ? "body" : "not skin (skipped)"));
                    if (region == 0 || !Wanted(region, s)) continue;
                    var rd = FindRend(r, sub);
                    if (rd == null || rd.Mat != mat)
                    {
                        if (rd != null) RemoveRend(rd);
                        rd = new Rend { R = r, Sub = sub, Region = region, Name = r.name + (mats.Length > 1 ? "#" + sub : ""), Mat = mat };
                        var sh = GetSheet(mat, region == 1);
                        if (sh.Skip) continue;              // no normal map property: no renderer for it
                        rd.S = sh;
                        sh.Rends.Add(rd);
                        _rends.Add(rd);
                    }
                    rd.Region = region;
                    keep.Add(rd);
                }
            }
            for (int i = _rends.Count - 1; i >= 0; i--)
                if (!keep.Contains(_rends[i])) RemoveRend(_rends[i]);
            for (int i = _sheets.Count - 1; i >= 0; i--)
                if (_sheets[i].Rends.Count == 0) { RestoreSheet(_sheets[i]); ReleaseSheet(_sheets[i]); _sheets.RemoveAt(i); LogMemory(); }
        }
        int _lastDiscoverKey = -1;

        void RemoveRend(Rend rd)
        {
            _rends.Remove(rd);
            if (rd.S != null) rd.S.Rends.Remove(rd);
            DisposeRend(rd);
        }

        // ---- CPU side of the renderers: bakes, change detection, recapture ----

        void ServiceRends(Character her, WrinkleSettings s, float now)
        {
            if (s.Recapture) { s.Recapture = false; _recapAll = true; _recapAllWhy = "button"; }
            if (her.DirtyAt != _seenDirtyAt)
            {
                _seenDirtyAt = her.DirtyAt;
                _recaptureAt = her.DirtyAt + 1.1f;
            }
            if (_recaptureAt > 0f && now >= _recaptureAt) { _recaptureAt = 0f; _recapAll = true; _recapAllWhy = "character edited"; }
            int softVer = her.Soft != null ? her.Soft.BuildVersion : 0;
            bool checkBs = (_updateCount % 3) == 0;
            for (int i = 0; i < _rends.Count; i++)
            {
                var r = _rends[i];
                if (r.State != 1) continue;
                var sr = r.R;
                if (sr == null) continue;
                var m = sr.sharedMesh;
                string why = null;
                if (m == null) continue;
                if (m != r.Mesh) why = "mesh changed to " + m.name;
                else if (m.vertexCount != r.V || (int)(m.GetIndexCount(r.Sub) / 3) != r.T) why = "mesh topology changed (" + r.V + "v/" + r.T + "t -> " + m.vertexCount + "v/" + (m.GetIndexCount(r.Sub) / 3) + "t)";
                else if (softVer != r.SoftVer) why = "soft zones rebuilt";
                if (why != null) { Rebake(r, why); continue; }
                if (_recapAll) { r.NeedRecapture = true; r.RecaptureWhy = _recapAllWhy; }
                else if (MorphState.BindVersion != r.BindVer) { r.NeedRecapture = true; r.RecaptureWhy = "bind poses moved"; }
                else if (checkBs && !r.NeedRecapture && now >= r.BsSuspendUntil && BlendshapesMoved(r))
                {
                    if (now - r.LastRecapture >= 1f)
                    {
                        if (now - r.BsBurstStart > 12f) { r.BsBurstStart = now; r.BsBurst = 0; }
                        if (++r.BsBurst > 6)
                        {
                            r.BsSuspendUntil = now + 30f;
                            Plugin.Log.LogWarning("Wrinkles: renderer " + r.Name + "'s blendshape weights keep changing; not recapturing for 30 s");
                        }
                        else { r.NeedRecapture = true; r.RecaptureWhy = BsWhy; }
                    }
                }
            }
            _recapAll = false;
            // at most one heavy CPU job per frame: a recapture, else a bake
            for (int i = 0; i < _rends.Count; i++)
            {
                var r = _rends[i];
                if (r.State == 1 && r.NeedRecapture) { Recapture(r, her); return; }
            }
            for (int i = 0; i < _rends.Count; i++)
            {
                var r = _rends[i];
                if (r.State == 0 && now >= r.RetryAt) { Bake(r, her, now); return; }
                if (r.State == -1 && now >= r.RetryAt) { r.State = 0; }
            }
        }

        bool BlendshapesMoved(Rend r)
        {
            var m = r.Mesh;
            var snap = r.BsSnap;
            if (m == null || snap == null) return false;
            int n = Mathf.Min(snap.Length, m.blendShapeCount);
            bool moved = false;
            for (int k = 0; k < n; k++)
            {
                float w = r.R.GetBlendShapeWeight(k);
                if (Mathf.Abs(w - snap[k]) > 0.5f) { moved = true; break; }   // Unity's 0-100 scale
            }
            return moved;
        }

        void SnapBlendshapes(Rend r)
        {
            var m = r.Mesh;
            int n = m != null ? m.blendShapeCount : 0;
            if (r.BsSnap == null || r.BsSnap.Length != n) r.BsSnap = new float[n];
            for (int k = 0; k < n; k++) r.BsSnap[k] = r.R.GetBlendShapeWeight(k);
        }

        void Rebake(Rend r, string why)
        {
            Plugin.Log.LogInfo("Wrinkles: re-bake " + r.Name + " (" + why + ")");
            ReleaseBuffers(r);
            r.State = 0;
            r.RetryAt = 0f;
            r.NeedRecapture = false;
            r.Layout = 0; r.ScaleChecked = false;
            if (r.S != null) { r.S.Dirty = true; }
        }

        void Bake(Rend r, Character her, float now)
        {
            var sw = Stopwatch.StartNew();
            string err = null;
            BakeResult b = null;
            try { b = WrinkleBake.Bake(r.R, r.Sub, her, r.S != null ? r.S.Layout : null, out err); }
            catch (Exception e) { err = "exception: " + e.Message; Plugin.Log.LogError("Wrinkles: bake of " + r.Name + " threw: " + e); }
            sw.Stop();
            if (b != null)
            {
                string bad = Validate(b, r);
                if (bad != null) { err = bad; b = null; }
            }
            if (b == null)
            {
                r.State = -1;
                bool otherUv = err != null && err.StartsWith("its UVs");
                r.RetryAt = now + (otherUv ? 120f : 15f);
                if (err != r.LastError)
                {
                    r.LastError = err;
                    if (otherUv) Plugin.Log.LogInfo("Wrinkles: renderer " + r.Name + " has no wrinkles: " + err + " (the maps are authored on her export by tools/wrinkles/author.py)");
                    else Plugin.Log.LogWarning("Wrinkles: renderer " + r.Name + " not baked: " + err + " (retrying every 15 s)");
                }
                return;
            }
            r.B = b;
            r.Mesh = b.Mesh != null ? b.Mesh : r.R.sharedMesh;
            r.V = b.V; r.T = b.T; r.RT = b.RT;
            r.BakeMs = sw.Elapsed.TotalMilliseconds;
            Upload(r, b);
            if (!r.RawAcquired && r.RT > 0) { RawTarget.Acquire(r.R); r.RawAcquired = true; }
            r.Layout = 0; r.ScaleChecked = false;
            r.State = 1;
            r.LastError = "";
            r.SoftVer = her.Soft != null ? her.Soft.BuildVersion : 0;
            r.BindVer = MorphState.BindVersion;
            r.LastRecapture = now;
            SnapBlendshapes(r);
            if (r.S != null && r.S.Built)
            {
                Plugin.Log.LogInfo("Wrinkles: sheet '" + r.S.Name + "' + renderer " + r.Name + ", " + r.V + " verts " + r.T + " tris, " + b.Note + ", baked in " + r.BakeMs.ToString("0") + " ms");
                LogMemory();
                r.S.Dirty = true;
            }
        }

        static string Validate(BakeResult b, Rend r)
        {
            if (b.V <= 0 || b.T <= 0) return "empty bake (" + b.V + " verts, " + b.T + " tris)";
            if (b.Tris == null || b.Tris.Length != 3 * b.RT) return "Tris length " + (b.Tris == null ? 0 : b.Tris.Length) + " != 3*" + b.RT;
            if (b.RegTri == null || b.RegTri.Length != 8 * b.RT) return "RegTri length != 8*RT";
            for (int i = 0; i < b.Tris.Length; i++) if (b.Tris[i] < 0 || b.Tris[i] >= b.V) return "region triangle vertex out of range";
            var m = r.R.sharedMesh;
            if (m != null && m.vertexCount != b.V) return "baked vertex count " + b.V + " != renderer mesh " + m.vertexCount;
            return null;
        }

        void Upload(Rend r, BakeResult b)
        {
            ReleaseBuffers(r);
            if (b.RT == 0) return;
            r.Tris = Buf(b.Tris.Length, 4, b.Tris);
            r.RegTri = Buf(2 * b.RT, 16, b.RegTri);
            r.Bytes = (long)(b.Tris.Length + b.RegTri.Length) * 4;
        }

        static GraphicsBuffer Buf(int count, int stride, Array data)
        {
            var g = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(1, count), stride);
            if (data != null && data.Length > 0) g.SetData(data);
            return g;
        }

        void ReleaseBuffers(Rend r)
        {
            Rel(ref r.Tris); Rel(ref r.RegTri);
            r.Bytes = 0;
        }

        static void Rel(ref GraphicsBuffer g)
        {
            if (g != null) { try { g.Release(); } catch { } g = null; }
        }

        void DisposeRend(Rend r)
        {
            r.Alive = false;
            r.State = 0;
            ReleaseBuffers(r);
            if (r.RawAcquired)
            {
                r.RawAcquired = false;
                try { if (r.R != null) RawTarget.Release(r.R); } catch (Exception e) { Plugin.Log.LogWarning("Wrinkles: releasing Raw on " + r.Name + " failed: " + e.Message); }
            }
        }

        // Rest frames against the current neutral (same topology and region triangles).
        void Recapture(Rend r, Character her)
        {
            r.NeedRecapture = false;
            string why = r.RecaptureWhy;
            float now = Time.unscaledTime;
            var sw = Stopwatch.StartNew();
            string err;
            bool ok = false;
            try { ok = WrinkleBake.Recapture(r.B, her, out err); }
            catch (Exception e) { err = "exception: " + e.Message; }
            sw.Stop();
            r.LastRecapture = now;
            r.BindVer = MorphState.BindVersion;
            SnapBlendshapes(r);
            if (!ok)
            {
                Plugin.Log.LogWarning("Wrinkles: recapture of " + r.Name + " failed (" + err + "); re-baking");
                Rebake(r, "recapture failed");
                return;
            }
            var b = r.B;
            if (r.RegTri != null && b.RT > 0) r.RegTri.SetData(b.RegTri);
            if (r.S != null) r.S.Dirty = true;
            Plugin.Log.LogInfo("Wrinkles: recapture " + r.Name + " (" + why + ") in " + sw.Elapsed.TotalMilliseconds.ToString("0") + " ms");
        }

        // ---- sheets: normal RT, assignment, base tracking ----

        void ServiceSheets(Character her, WrinkleSettings s, float now)
        {
            if (_flipState != 2) return;
            bool check = (_updateCount & 3) == 0;
            for (int i = _sheets.Count - 1; i >= 0; i--)
            {
                var sh = _sheets[i];
                if (sh.Skip || sh.Mat == null) continue;
                int ready = 0;
                for (int k = 0; k < sh.Rends.Count; k++) if (sh.Rends[k].Ready) ready++;
                if (!sh.Built)
                {
                    if (ready > 0 && ready == CountBaked(sh))
                    {
                        // nothing of these renderers lies under a region (the neck mesh alone on the body atlas while Body is off): no copy
                        int rt = 0;
                        for (int k = 0; k < sh.Rends.Count; k++) if (sh.Rends[k].Ready) rt += sh.Rends[k].RT;
                        if (rt == 0 && !(sh.Muscle != null && s.Muscles.On))
                        {
                            if (!sh.Empty) Plugin.Log.LogInfo("Wrinkles: sheet '" + sh.Name + "': no wrinkle region on its renderers; left alone");
                            sh.Empty = true;
                            continue;
                        }
                        sh.Empty = false;
                        BuildSheet(sh, her, s, now);
                    }
                    continue;
                }
                UpdateActs(sh, s, now);
                UpdateMuscles(sh, her, s, now);
                if (!check) continue;
                // base normal: Morph's baked one when it offers one, else whatever the slot holds that isn't ours
                Texture slot = sh.Mat.GetTexture(sh.Prop);
                Texture want = sh.Base;
                Texture nf = NormalFor(her, sh.Mat);
                if (nf != null && !Owns(nf)) want = nf;
                else if (slot != null && !Owns(slot)) { want = slot; sh.Orig = slot; }
                else if (nf == null && Owns(slot)) want = Usable(sh.Orig) ?? sh.Base;   // Morph's baked normal went away: back to her own, not the stale one
                bool resize = false;
                if (want != sh.Base)
                {
                    sh.Base = want;
                    sh.Dirty = true;
                    int res = TargetSize(sh, want, s);
                    resize = res != sh.Res;
                    Plugin.Log.LogInfo("Wrinkles: sheet '" + sh.Name + "' base normal now " + Describe(want));
                }
                else if (TargetSize(sh, sh.Base, s) != sh.Res) resize = true;
                if (resize) MakeNormalRT(sh, s);
                if (slot != sh.Normal) sh.Mat.SetTexture(sh.Prop, sh.Normal);   // self-heal: the game or Morph put its own back
                if (Mathf.Abs(sh.StrengthApplied - s.Strength) > 0.001f) sh.Dirty = true;
                if (sh.Albedo != null && Mathf.Abs(sh.CavityApplied - s.Cavity) > 0.001f) sh.Dirty = true;
                ServiceAlbedo(sh, her, s, now);
                if (sh.Albedo != null && sh.DebugApplied != sh.DebugMode) sh.Dirty = true;
                var ms = s.Muscles;
                bool mOn = sh.Muscle != null && ms.On;
                if (mOn != sh.MOnApplied || (mOn && (Mathf.Abs(sh.MStrengthApplied - ms.Strength) > 0.001f || Mathf.Abs(sh.MShadeApplied - ms.Shade) > 0.001f || sh.MDebugApplied != ms.Debug))) sh.Dirty = true;
            }
        }

        // 0.10.0: the muscles' activations from her pose (MusclePose), faded with attack / release. A muscle whose area is off goes to 0;
        // the rest sit at the resting tone (Fitness) plus the pose's tension x Gain. The sheet blends again when one moves by 1/32, at most
        // every MuscleMinInterval s (her muscles change slowly; a raster of the 2048 normal RT costs GPU time every time).
        void UpdateMuscles(Sheet sh, Character her, WrinkleSettings s, float now)
        {
            var M = sh.Muscle;
            var ms = s.Muscles;
            if (M == null || !ms.On) return;
            float dt = sh.MLastAt > 0f ? Mathf.Clamp(now - sh.MLastAt, 0f, 0.1f) : 0f;
            sh.MLastAt = now;
            bool posed = MusclePose.Tension(her, M, sh.MTension);
            float ka = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, ms.Attack)), kr = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, ms.Release));
            float fit = Mathf.Clamp01(ms.Fitness);
            bool moved = false;
            for (int k = 0; k < M.Count && k < MaxRegions; k++)
            {
                string area = M.Area[k];
                bool on = area == "core" ? ms.Core : area == "back" ? ms.Back : area == "legs" ? ms.Legs : area == "arms" ? ms.Arms : true;
                float tens = posed ? sh.MTension[k] : 0f;
                if (tens > sh.MPeak[k]) sh.MPeak[k] = tens;
                float target = on ? fit + (1f - fit) * Mathf.Clamp01(ms.Gain * tens) : 0f;
                float a = sh.MAct[k];
                a = dt == 0f ? target : a + (target - a) * (target > a ? ka : kr);    // first frame: straight to the target
                sh.MAct[k] = a;
                if (Mathf.Abs(a - sh.MActSent[k]) >= MuscleStep || (a == 0f) != (sh.MActSent[k] == 0f)) moved = true;
            }
            MuscleFlex.Publish(M, sh.MAct);   // 0.11.8: the same activations push her muscles out as geometry (GpuContact) and firm them under a hand
            if (moved && now - sh.MLastDirty >= MuscleMinInterval)
            {
                sh.MLastDirty = now;
                sh.Dirty = true;
                sh.LastChange = now;
                if (sh.Asleep) sh.Asleep = false;
            }
            if (now >= _nextMusclePeak)
            {
                _nextMusclePeak = now + PeakLogEvery;
                var idx = new List<int>();
                for (int k = 0; k < M.Count; k++) if (sh.MPeak[k] >= 0.1f) idx.Add(k);
                idx.Sort((x, y) => sh.MPeak[y].CompareTo(sh.MPeak[x]));
                var sb = new StringBuilder();
                for (int i = 0; i < idx.Count && i < 10; i++)
                    sb.Append(sb.Length > 0 ? ", " : "").Append(M.MuscleName[idx[i]]).Append(' ').Append(sh.MPeak[idx[i]].ToString("0.00"));
                string line = sb.Length > 0 ? sb.ToString() : "none";
                if (line != _lastMusclePeak)
                    Plugin.Log.LogInfo("Muscles: peak tension over " + PeakLogEvery.ToString("0") + " s: " + line + " (now: " + MusclePose.PoseLine() + ")");
                _lastMusclePeak = line;
                for (int k = 0; k < MaxRegions; k++) sh.MPeak[k] = 0f;
            }
        }
        float _nextMusclePeak;
        string _lastMusclePeak = "";

        // The muscles tensed now, for the panel: "abs 80%, quads_L 55%" (tension, strongest first), "" when none.
        public string ActiveMuscles()
        {
            var parts = new List<KeyValuePair<float, string>>();
            for (int i = 0; i < _sheets.Count; i++)
            {
                var sh = _sheets[i];
                if (!sh.Built || sh.Muscle == null) continue;
                for (int k = 0; k < sh.Muscle.Count; k++)
                    if (sh.MTension[k] >= 0.1f) parts.Add(new KeyValuePair<float, string>(sh.MTension[k], sh.Muscle.MuscleName[k] + " " + (sh.MTension[k] * 100f).ToString("0") + "%"));
            }
            parts.Sort((a, b) => b.Key.CompareTo(a.Key));
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Count && i < 6; i++) sb.Append(i > 0 ? ", " : "").Append(parts[i].Value);
            return sb.ToString();
        }

        int CountBaked(Sheet sh)
        {
            int n = 0;
            for (int k = 0; k < sh.Rends.Count; k++) if (sh.Rends[k].State == 1 || sh.Rends[k].State == 0) n++;   // still to bake or baked; failed ones don't hold the sheet
            return n;
        }

        void BuildSheet(Sheet sh, Character her, WrinkleSettings s, float now)
        {
            Texture slot = sh.Mat.GetTexture(sh.Prop);
            Texture nf = NormalFor(her, sh.Mat);
            // 0.8.0: SkinMarks may hold the slot with its own normal copy: what it has under it is the base then
            Texture ext = slot != null && Owns(slot) ? ExternalBaseOf(sh.Mat, sh.Prop) : null;
            sh.Base = nf != null && !Owns(nf) ? nf : (slot != null && !Owns(slot) ? slot : ext);
            sh.Orig = (slot != null && !Owns(slot) && slot != nf) ? slot : (OriginalVia(her != null ? her.Morph : null, sh.Mat, sh.Prop) ?? ext);
            if (sh.Sums == null) sh.Sums = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2 * MaxRegions, 4);
            sh.Inflight = 0;
            for (int k = 0; k < MaxRegions; k++) { sh.Comp[k] = 0f; sh.Act[k] = 0f; sh.ActSent[k] = 0f; sh.Peak[k] = 0f; sh.Idle[k] = 0f; sh.IdleSet[k] = false; sh.MAct[k] = 0f; sh.MActSent[k] = 0f; sh.MTension[k] = 0f; }
            sh.MLastAt = 0f; sh.MOnApplied = false;
            sh.LastActAt = 0f;
            sh.NextPeakLog = now + PeakLogEvery;
            MakeNormalRT(sh, s);
            sh.Built = true;
            sh.LastChange = now;
            sh.Asleep = false;
            sh.Culled = false;
            sh.LastComputeAt = 0f;
            Raster(sh, Time.frameCount, true);   // the normal RT starts as a copy of the base
            sh.Mat.SetTexture(sh.Prop, sh.Normal);
            sh.Dirty = true;
            var names = new StringBuilder();
            var notes = new StringBuilder();
            int v = 0, t = 0;
            double ms = 0.0;
            for (int i = 0; i < sh.Rends.Count; i++)
            {
                var r = sh.Rends[i];
                if (!r.Ready) continue;
                if (names.Length > 0) { names.Append(", "); notes.Append("; "); }
                names.Append(r.Name);
                notes.Append(r.B.Note);
                v += r.V; t += r.T; ms += r.BakeMs;
            }
            Plugin.Log.LogInfo("Wrinkles: sheet '" + sh.Name + "' base " + Describe(sh.Base) + ", renderers [" + names + "], " + v + " verts " + t + " tris, " + notes + ", baked in " + ms.ToString("0") + " ms");
            LogMemory();
        }

        static string Describe(Texture t)
        {
            if (t == null) return "none (flat)";
            string fmt = t is Texture2D ? ((Texture2D)t).format.ToString() : t is RenderTexture ? ((RenderTexture)t).format.ToString() : t.GetType().Name;
            return (string.IsNullOrEmpty(t.name) ? "<unnamed>" : t.name) + " " + t.width + "x" + t.height + " fmt " + fmt;
        }

        // Normal RT size: the base's size, raised to the authored creases' size (her 1024 face normal map would halve the 2048 creases),
        // capped by Resolution (0 = 4096); body sheets at most 2048.
        static int TargetSize(Sheet sh, Texture b, WrinkleSettings s)
        {
            int bw = b != null ? b.width : 1024, bh = b != null ? b.height : 1024;
            int cap = s.Resolution <= 0 ? 4096 : s.Resolution;
            if (!sh.Face) cap = Math.Min(cap, 2048);
            int mx = Math.Max(bw, bh);
            int dm = sh.Layout != null && sh.Layout.Detail != null ? Math.Max(sh.Layout.Detail.width, sh.Layout.Detail.height) : 0;
            if (mx < dm) { float g = (float)dm / mx; bw = Mathf.RoundToInt(bw * g); bh = Mathf.RoundToInt(bh * g); mx = Math.Max(bw, bh); }
            if (mx > cap) { float f = (float)cap / mx; bw = Math.Max(64, Mathf.RoundToInt(bw * f)); bh = Math.Max(64, Mathf.RoundToInt(bh * f)); }
            return Math.Max(64, bw) * 65536 + Math.Max(64, bh);
        }

        void MakeNormalRT(Sheet sh, WrinkleSettings s)
        {
            int packed = TargetSize(sh, sh.Base, s);
            int w = packed / 65536, h = packed % 65536;
            var old = sh.Normal;
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                name = "SfMorph wrinkles " + sh.Name, useMipMap = true, autoGenerateMips = false, filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Repeat, anisoLevel = sh.Base != null ? Math.Max(1, sh.Base.anisoLevel) : 4, hideFlags = HideFlags.HideAndDontSave
            };
            rt.Create();
            _owned.Add(rt.GetInstanceID());
            sh.Normal = rt;
            sh.Res = packed;
            if (old != null)
            {
                if (sh.Mat != null && sh.Mat.GetTexture(sh.Prop) == old) sh.Mat.SetTexture(sh.Prop, rt);
                DestroyRT(old);
            }
            sh.Dirty = true;
            if (sh.Built) { Raster(sh, Time.frameCount, true); sh.Dirty = true; LogMemory(); }
        }

        static void DestroyRT(RenderTexture rt)
        {
            if (rt == null) return;
            _owned.Remove(rt.GetInstanceID());
            if (rt.IsCreated()) rt.Release();
            UnityEngine.Object.Destroy(rt);
        }

        // 0.8.4: the sheet's albedo copy in the albedo slot while Cavity > 0 or a debug view is on: base x SkinMarks' marks (body sheets) x crease
        // darkening, + the debug tint. Her skin shader's subsurface scattering blurs the folds' normal-map detail out of the diffuse light; a
        // darker crease in the skin colour survives it. The base is what the slot held (SkinMarks' copy: what it has under it).
        // Morph's Characters tab skips a slot holding our texture: when its skin state changes with our copy in the slot, the base goes back for
        // its next 2 s pass; and while a character skin is on, a texture someone else puts in the slot is taken over only once it has stayed
        // there HandBack seconds (Morph may be about to replace it).
        void ServiceAlbedo(Sheet sh, Character her, WrinkleSettings s, float now)
        {
            int mode = sh.AlbedoProp != null && s.On ? s.Debug : 0;
            bool cav = sh.AlbedoProp != null && _albedoPass && ((s.On && s.Cavity > 0.001f)
                       || (sh.Muscle != null && s.Muscles.On && (s.Muscles.Shade > 0.001f || s.Muscles.Debug > 0)));   // 0.10.0: muscle shade / debug view
            if (mode == 0 && !cav)
            {
                if (sh.Albedo != null) RestoreAlbedo(sh);
                return;
            }
            var mat = sh.Mat;
            Texture slot = mat.GetTexture(sh.AlbedoProp);
            bool skinOn;
            int sig = SkinSig(her, mat, out skinOn);
            if (sh.Albedo == null)
            {
                Texture b = slot != null && !Owns(slot) ? slot : (slot != null ? ExternalBaseOf(mat, sh.AlbedoProp) : null);   // SkinMarks' copy: what it has under it
                if (b == null && (slot != null || mode == 0))
                {
                    // a render texture of another system we can't see under, or no texture at all (nothing to shade): leave the slot alone
                    if (_logged.Add("noalb|" + sh.Name))
                        Plugin.Log.LogInfo("Wrinkles: sheet '" + sh.Name + "': albedo " + sh.AlbedoProp + (slot != null ? " holds a render texture of another system" : " is empty") + "; no crease shade on it");
                    return;
                }
                sh.AlbedoBase = b;
                sh.AlbedoOrig = OriginalVia(her != null ? her.Morph : null, mat, sh.AlbedoProp) ?? b;
                sh.SkinSig = sig;
                sh.Pending = null;
                sh.DebugMode = mode;
                MakeAlbedoRT(sh, s);
                Raster(sh, Time.frameCount, false);   // filled before it goes into the slot
                mat.SetTexture(sh.AlbedoProp, sh.Albedo);
                sh.AlbedoHeld = true;
                Plugin.Log.LogInfo("Wrinkles: sheet '" + sh.Name + "' albedo copy: " + sh.AlbedoProp + " '" + Describe(b) + "' -> RT " + sh.Albedo.width + "x" + sh.Albedo.height
                    + (cav ? ", crease shade " + s.Cavity.ToString("0.00") : "") + (mode != 0 ? ", debug view " + (mode == 2 ? "regions" : "activation") : "") + (skinOn ? ", character skin on" : ""));
                LogMemory();
                return;
            }
            if (slot == sh.Albedo)
            {
                sh.Pending = null;
                if (sig != sh.SkinSig)
                {
                    // the Characters tab switched a skin or the character: Morph skipped our slot; give it the base for its next pass
                    sh.SkinSig = sig;
                    mat.SetTexture(sh.AlbedoProp, Usable(sh.AlbedoBase) ?? Usable(sh.AlbedoOrig));
                    sh.AlbedoHeld = false;
                    sh.Pending = mat.GetTexture(sh.AlbedoProp);
                    sh.PendingSince = now;
                    Plugin.Log.LogInfo("Wrinkles: sheet '" + sh.Name + "': skin textures changed; albedo slot handed back for Morph's next pass");
                    return;
                }
            }
            else
            {
                sh.SkinSig = sig;
                if (slot != null && Owns(slot))
                {
                    // another system's render texture (SkinMarks' copy, before it saw ours): what it has under it is the base
                    var eb = ExternalBaseOf(mat, sh.AlbedoProp);
                    if (eb != null && eb != sh.AlbedoBase) { sh.AlbedoBase = eb; sh.Dirty = true; }
                }
                else if (slot != null)
                {
                    // the game (DefaultSetting) or Morph put a texture in: shade over it; with a character skin on (or the slot handed back)
                    // only once it has stayed, so Morph's own pass can replace the game's texture first
                    if (skinOn || !sh.AlbedoHeld)
                    {
                        if (slot != sh.Pending) { sh.Pending = slot; sh.PendingSince = now; return; }
                        if (now - sh.PendingSince < HandBack) return;
                    }
                    sh.Pending = null;
                    if (slot != sh.AlbedoBase)
                    {
                        Plugin.Log.LogInfo("Wrinkles: sheet '" + sh.Name + "' albedo base now " + Describe(slot));
                        sh.AlbedoBase = slot;
                        sh.Dirty = true;
                    }
                    sh.AlbedoOrig = OriginalVia(her != null ? her.Morph : null, mat, sh.AlbedoProp) ?? sh.AlbedoOrig ?? slot;
                }
                // empty slot: someone cleared it; ours goes back over the last base
            }
            if (AlbedoSize(sh.AlbedoBase, s) != sh.Albedo.width * 65536 + sh.Albedo.height) MakeAlbedoRT(sh, s);   // a bigger character skin, a Resolution change
            if (sh.DebugMode != mode) { sh.DebugMode = mode; sh.Dirty = true; }
            if (slot != sh.Albedo)
            {
                if (sh.Dirty) Raster(sh, Time.frameCount, false);
                mat.SetTexture(sh.AlbedoProp, sh.Albedo);
                sh.AlbedoHeld = true;
            }
        }

        // Albedo copy size: the base's size capped by Resolution (0 = 4096; the copy replaces her skin colour, so no 2048 body cap).
        static int AlbedoSize(Texture b, WrinkleSettings s)
        {
            int bw = b != null ? b.width : 1024, bh = b != null ? b.height : 1024;
            int cap = s.Resolution <= 0 ? 4096 : s.Resolution;
            int mx = Math.Max(bw, bh);
            if (mx > cap) { float f = (float)cap / mx; bw = Math.Max(64, Mathf.RoundToInt(bw * f)); bh = Math.Max(64, Mathf.RoundToInt(bh * f)); }
            return Math.Max(64, bw) * 65536 + Math.Max(64, bh);
        }

        void MakeAlbedoRT(Sheet sh, WrinkleSettings s)
        {
            int packed = AlbedoSize(sh.AlbedoBase, s);
            int w = packed / 65536, h = packed % 65536;
            var old = sh.Albedo;
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "SfMorph wrinkles albedo " + sh.Name, useMipMap = true, autoGenerateMips = false, filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Repeat, anisoLevel = sh.AlbedoBase != null ? Math.Max(1, sh.AlbedoBase.anisoLevel) : 4, hideFlags = HideFlags.HideAndDontSave
            };
            rt.Create();
            _owned.Add(rt.GetInstanceID());
            sh.Albedo = rt;
            sh.Dirty = true;
            if (old != null)
            {
                Raster(sh, Time.frameCount, false);
                if (sh.Mat != null && sh.AlbedoProp != null && sh.Mat.GetTexture(sh.AlbedoProp) == old) sh.Mat.SetTexture(sh.AlbedoProp, rt);
                DestroyRT(old);
                Plugin.Log.LogInfo("Wrinkles: sheet '" + sh.Name + "' albedo copy now " + w + "x" + h);
                LogMemory();
            }
        }

        void RestoreAlbedo(Sheet sh)
        {
            if (sh.Albedo == null) return;
            if (sh.Mat != null && sh.AlbedoProp != null && sh.Mat.GetTexture(sh.AlbedoProp) == sh.Albedo)
            {
                // never write an owned / destroyed texture back: fall to Morph's original when AlbedoBase is unusable
                Texture back = Usable(sh.AlbedoBase) ?? Usable(OriginalVia(_her != null ? _her.Morph : null, sh.Mat, sh.AlbedoProp)) ?? Usable(sh.AlbedoOrig);
                sh.Mat.SetTexture(sh.AlbedoProp, back);
            }
            DestroyRT(sh.Albedo);
            sh.Albedo = null;
            sh.AlbedoBase = null;
            sh.AlbedoOrig = null;
            sh.AlbedoHeld = false;
            sh.Pending = null;
            sh.DebugMode = 0;
            sh.Dirty = true;
            LogMemory();
        }

        // Morph's skin state as a number (which skins are on, for which character; 0 = no character skin): its _texOn / _texKey / _set fields
        // by reflection (Morph.cs has no accessor for them), else the baked normal it reports for this material.
        int SkinSig(Character her, Material mat, out bool skinOn)
        {
            skinOn = false;
            var morph = her != null ? her.Morph : null;
            if (morph == null) return 0;
            try
            {
                var t = morph.GetType();
                if (t != _skType)
                {
                    _skType = t;
                    const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    _skOn = t.GetField("_texOn", F); _skKey = t.GetField("_texKey", F); _skSet = t.GetField("_set", F);
                    if (_skOn != null && _skOn.FieldType != typeof(bool)) _skOn = null;
                    if (_skKey != null && _skKey.FieldType != typeof(int)) _skKey = null;
                    Plugin.Log.LogInfo("Wrinkles: Morph's skin state " + (_skOn != null && _skKey != null ? "found" : "not found (watching its baked normal maps instead)"));
                }
                if (_skOn != null && _skKey != null)
                {
                    skinOn = (bool)_skOn.GetValue(morph);
                    if (!skinOn) return 0;
                    object set = _skSet != null ? _skSet.GetValue(morph) : null;
                    return ((int)_skKey.GetValue(morph) * 397) ^ (set != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(set) : 0) | 1;
                }
            }
            catch (Exception e)
            {
                _skOn = null; _skKey = null;
                Plugin.Log.LogWarning("Wrinkles: reading Morph's skin state failed (" + e.Message + "); watching its baked normal maps instead");
            }
            var nf = NormalFor(her, mat);
            skinOn = nf != null;
            return nf != null ? nf.GetInstanceID() | 1 : 0;
        }
        Type _skType;
        FieldInfo _skOn, _skKey, _skSet;

        // t if it is a live texture that isn't one of ours, else null.
        static Texture Usable(Texture t) { return t != null && !Owns(t) ? t : null; }

        // Morph.OriginalTexture(Material, string) (her own texture, whatever the converted character put there since), by reflection; null if absent.
        Texture OriginalVia(object morph, Material mat, string prop)
        {
            try
            {
                if (morph == null || mat == null || prop == null) return null;
                var t = morph.GetType();
                if (t != _otType)
                {
                    _otType = t;
                    _otMethod = t.GetMethod("OriginalTexture", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(Material), typeof(string) }, null);
                    if (_otMethod != null && !typeof(Texture).IsAssignableFrom(_otMethod.ReturnType)) _otMethod = null;
                }
                if (_otMethod == null) return null;
                var o = _otMethod.Invoke(morph, new object[] { mat, prop }) as Texture;
                return Usable(o);
            }
            catch (Exception e)
            {
                _otMethod = null;
                Plugin.Log.LogWarning("Wrinkles: Morph.OriginalTexture failed (" + e.Message + ")");
                return null;
            }
        }
        MethodInfo _otMethod;
        Type _otType;

        // Morph.NormalFor(Material) (the baked normal when its textures are on). 0.9.9: a direct call (it was a reflection Invoke with a new
        // argument array every 4th frame per sheet; MorphState is in this assembly).
        Texture NormalFor(Character her, Material mat)
        {
            try
            {
                var morph = her != null ? her.Morph : null;
                return morph != null ? morph.NormalFor(mat) : null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Wrinkles: Morph.NormalFor failed (" + e.Message + "); using the material's current normal map");
                return null;
            }
        }

        // ---- culling ----

        void UpdateCull(Character her, float now)
        {
            if (now < _nextCull) return;
            _nextCull = now + 0.25f;
            if (_cam == null || !_cam.isActiveAndEnabled)
            {
                _cam = Camera.main;
                if (_cam == null && now >= _nextCamFind)
                {
                    _nextCamFind = now + 2f;
                    foreach (var c in Camera.allCameras) if (c != null && c.stereoEnabled && c.cameraType == CameraType.Game) { _cam = c; break; }
                }
            }
            Transform head = null;
            Transform hb;
            if (her.Bones.TryGetValue("Head_M", out hb)) head = hb;
            else if (her.Heads.Count > 0) head = her.Heads[0];
            else head = her.Root;
            _farAway = false;
            if (_cam != null && head != null && _s != null)
                _farAway = (_cam.transform.position - head.position).sqrMagnitude > _s.CullDistance * _s.CullDistance;
            for (int i = 0; i < _sheets.Count; i++)
            {
                var sh = _sheets[i];
                bool vis = false;
                for (int k = 0; k < sh.Rends.Count && !vis; k++)
                {
                    var sr = sh.Rends[k].R;
                    if (sr != null && sr.isVisible) vis = true;
                }
                sh.Culled = _farAway || !vis;
            }
        }

        // ---- status ----

        void UpdateStatus()
        {
            if (_dead) return;
            int rn = 0, culled = 0, asleep = 0, built = 0, noVb = 0;
            for (int i = 0; i < _rends.Count; i++) { if (_rends[i].Ready) rn++; if (_rends[i].NoVb >= 300) noVb++; }
            for (int i = 0; i < _sheets.Count; i++)
            {
                var sh = _sheets[i];
                if (!sh.Built) continue;
                built++;
                if (sh.Culled) culled++; else if (sh.Asleep) asleep++;
            }
            if (!_active) { Status = ""; return; }
            if (_flipState != 2) Status = "Wrinkles: on, self-test running";
            else if (_rends.Count == 0) Status = "Wrinkles: on, no skin renderers found";
            else
            {
                string act = ActiveRegions();
                string mact = _s != null && _s.Muscles.On ? ActiveMuscles() : "";
                if (mact.Length > 0) act = act.Length > 0 ? act + "; tensed: " + mact : "tensed: " + mact;
                Status = "Wrinkles: on, " + built + " sheets, " + rn + "/" + _rends.Count + " renderers baked" + (culled > 0 ? ", " + culled + " culled" : "") + (asleep > 0 ? ", " + asleep + " idle" : "")
                    + (noVb > 0 ? ", " + noVb + " without skinned vertex buffer (CPU skinning?)" : "") + (act.Length > 0 ? "; active: " + act : "");
            }
        }

        void LogMemory()
        {
            long buf = 0, rts = 0, dbg = 0;
            for (int i = 0; i < _rends.Count; i++) buf += _rends[i].Bytes;
            for (int i = 0; i < _sheets.Count; i++)
            {
                var sh = _sheets[i];
                if (sh.Normal != null) rts += (long)sh.Normal.width * sh.Normal.height * 4 * 4 / 3;
                if (sh.Albedo != null) dbg += (long)sh.Albedo.width * sh.Albedo.height * 4 * 4 / 3;
            }
            long tot = buf + rts + dbg;
            if (tot == _lastMemLog) return;
            _lastMemLog = tot;
            Plugin.Log.LogInfo("Wrinkles: memory: buffers " + MB(buf) + " MB, normal RTs " + MB(rts) + " MB" + (dbg > 0 ? ", albedo RTs " + MB(dbg) + " MB" : "") + ", total " + MB(tot) + " MB");
        }

        static string MB(long b) { return (b / 1048576.0).ToString("0.0"); }

        // ---- off / teardown ----

        // Settings turned it off: give the skin back (the baked data and buffers stay for a few seconds in case it is toggled again).
        void GoIdle(float now)
        {
            MusclePose.Release();
            _active = false;
            _offSince = now;
            for (int i = 0; i < _sheets.Count; i++) { RestoreSheet(_sheets[i]); }
            Plugin.Log.LogInfo("Wrinkles: off (skin slots restored)");
        }

        // Puts the original textures back and frees the sheet's render textures; the sheet can be built again.
        void RestoreSheet(Sheet sh)
        {
            try
            {
                if (sh.Mat != null && sh.Prop != null && sh.Normal != null)
                {
                    var cur = sh.Mat.GetTexture(sh.Prop);
                    if (cur == sh.Normal || Owns(cur))
                    {
                        // Morph's baked normal if it still offers one, else her own, else the last base; never a texture we own
                        Texture back = Usable(NormalFor(_her, sh.Mat)) ?? Usable(sh.Orig) ?? Usable(sh.Base);
                        sh.Mat.SetTexture(sh.Prop, back);
                    }
                }
                RestoreAlbedo(sh);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Wrinkles: restoring '" + sh.Name + "' failed: " + e.Message); }
            DestroyRT(sh.Normal);
            sh.Normal = null;
            sh.Built = false;
            sh.Res = 0;
            sh.RasterLogged = false;
        }

        // The sheet's GPU sums (a sheet being forgotten); a readback still in flight finds Sums null and drops its result.
        void ReleaseSheet(Sheet sh)
        {
            if (sh.Sums != null) { try { sh.Sums.Release(); } catch { } sh.Sums = null; }
            sh.Inflight = 0;
        }

        void DropAll()
        {
            for (int i = 0; i < _sheets.Count; i++) { RestoreSheet(_sheets[i]); ReleaseSheet(_sheets[i]); }
            _sheets.Clear();
            for (int i = 0; i < _rends.Count; i++) DisposeRend(_rends[i]);
            _rends.Clear();
            _lastDiscoverKey = -1;
            _nextDiscover = 0f;
            _logged.RemoveWhere(x => x.StartsWith("noprop|") || x.StartsWith("nolayout|"));
            _her = null;
        }

        void FreeShared()
        {
            _lastMemLog = -1;
        }

        void GoDead(string why)
        {
            _dead = true;
            Status = "Wrinkles: off: " + why;
            LastStatus = Status;
            Unsubscribe();
        }

        void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            GpuContact.BeforeImprint -= OnBeforeImprint;
            RenderPipelineManager.beginContextRendering -= OnBeginContext;
        }

        // Frees everything that touches Unity objects. Safe to call twice.
        void Teardown()
        {
            if (_tornDown) return;
            _tornDown = true;
            if (_live == this) _live = null;
            Unsubscribe();
            if (_inLoop) { _inLoop = false; try { RemoveStep(); } catch (Exception e) { Plugin.Log.LogWarning("Wrinkles: removing the loop step failed: " + e.Message); } }
            try { DropAll(); } catch (Exception e) { Plugin.Log.LogWarning("Wrinkles: restore failed: " + e.Message); }
            FreeShared();
            DestroyRT(_testA); _testA = null;
            DestroyRT(_testB); _testB = null;
            if (_cmd != null) { _cmd.Release(); _cmd = null; }
            if (_mat != null) { UnityEngine.Object.Destroy(_mat); _mat = null; }
            if (_flat != null) { UnityEngine.Object.Destroy(_flat); _flat = null; }
            _cs = null;
            _shader = null;
            if (_bundleHeld) { _bundleHeld = false; try { ShaderBundle.Release(); } catch (Exception e) { Plugin.Log.LogWarning("Wrinkles: releasing the shader bundle failed: " + e.Message); } }
        }

        public void Release()
        {
            if (_live == this) _live = null;
            try { Teardown(); }
            catch (Exception e) { Plugin.Log.LogWarning("Wrinkles: release failed: " + e.Message); }
            _released = true;
            _dead = true;
            _active = false;
            Status = "";
            Plugin.Log.LogInfo("Wrinkles: released");
        }

        // ---- PlayerLoop step (same pattern as GpuContact): right after PostLateUpdate.UpdateAllSkinnedMeshes ----

        static bool InsertStep(PlayerLoopSystem.UpdateFunction fn)
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var subs = root.subSystemList;
            if (subs == null) return false;
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type != typeof(PostLateUpdate) || subs[i].subSystemList == null) continue;
                var list = new List<PlayerLoopSystem>(subs[i].subSystemList);
                list.RemoveAll(x => x.type == typeof(WrinkleStep));   // a stale step of an earlier instance
                int at = list.FindIndex(x => x.type == typeof(PostLateUpdate.UpdateAllSkinnedMeshes));
                if (at < 0) return false;
                list.Insert(at + 1, new PlayerLoopSystem { type = typeof(WrinkleStep), updateDelegate = fn });
                subs[i].subSystemList = list.ToArray();
                root.subSystemList = subs;
                PlayerLoop.SetPlayerLoop(root);
                return true;
            }
            return false;
        }

        static void RemoveStep()
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var subs = root.subSystemList;
            if (subs == null) return;
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type != typeof(PostLateUpdate) || subs[i].subSystemList == null) continue;
                var list = new List<PlayerLoopSystem>(subs[i].subSystemList);
                if (list.RemoveAll(x => x.type == typeof(WrinkleStep)) == 0) return;
                subs[i].subSystemList = list.ToArray();
                root.subSystemList = subs;
                PlayerLoop.SetPlayerLoop(root);
                return;
            }
        }
    }
}
