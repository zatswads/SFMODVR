using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.Rendering;

namespace SfMorph
{
    // Post-skinning contact deformer on the GPU (0.7.3): her skinned vertices are pressed out of the hands' capsules by a compute
    // shader from sfmorph_shaders (tools/shaders). Runs once per frame, whichever comes first: a PlayerLoop step right after
    // PostLateUpdate.UpdateAllSkinnedMeshes or RenderPipelineManager.beginContextRendering (game camera only), straight
    // on the skinned vertex buffer of her body renderers (vertexBufferTarget |= Raw). Nothing here may throw into HDRP: any
    // exception in the callback logs, switches the deformer off and leaves the teardown to the next main-thread call.
    // PlayerLoop marker type of the fallback dispatch step (same pattern as ApplyStep).
    internal struct GpuContactStep { }

    internal class GpuContact
    {
        public string Status = "";
        // True once it switched itself off after an error (Status then starts with "GPU contact: off: ").
        public bool Dead { get { return _dead; } }
        // Why the last TryLoad returned null (same wording as Status).
        public static string LastStatus = "";
        // 0.7.4: raised once per frame right after the frame guard, before Imprint writes the skinned buffers (wrinkle strain
        // must read the clean skinning result).
        public static event System.Action<int> BeforeImprint;
        // 0.8.0: raised once per frame at the end of Run, after every renderer's chain (SkinMarks rasterises the marks from it).
        public static event System.Action<int> AfterImprint;
        // 0.8.0: bumped whenever a renderer's bake (and so its Canon/Marks buffers) is made or dropped: SkinMarks rebuilds its views on a change.
        public int BakeVersion;

        [StructLayout(LayoutKind.Sequential)]
        struct CapData { public Vector4 A, B; public uint C0, C1, C2, C3; }   // a.xyz + radius, b.xyz, c.x = skip mask (stride 48, mirrors the compute shader's Capsule)

        const int MaxCaps = 256;            // per renderer
        const int CapBufCount = 2048;       // all renderers' capsules of a frame, one upload
        const int CalibVerts = 256;
        // The best space must fit (median error below CalibAccept) and clearly beat every other: by an absolute margin (CalibSeparate) or, for a small
        // mesh (nipples: the root bone carries the breast size, so scaled and unscaled skinning differ by centimetres only), by a relative one.
        const float CalibAccept = 0.02f, CalibSeparate = 0.10f, CalibRelative = 8f, CalibRelFloor = 0.01f;
        const int CalibMaxTries = 3, CalibRetryFrames = 30;

        // ---- per renderer ----

        const int NullVbMax = 300;

        class RendState
        {
            public SkinnedMeshRenderer R;
            public string Name;
            public bool Alive = true;
            public bool RawHeld;                  // we hold a RawTarget reference (released when the renderer is dropped)
            public Mesh Mesh;
            public int Count;
            public int Layout;                    // 0 = not checked against a vertex buffer yet, 1 = ok, -1 = skipped
            public int Stride, PosOff, NrmOff;
            public GraphicsBuffer Mask;
            public int Space;                     // 0 = unknown, 1 = rootBone, 2 = transform, 3 = identity, -1 = calibration failed
            public int CalibState;                // 0 = idle, 1 = readback pending
            public int CalibTries, CalibNextFrame;
            public int NullVb;                    // 0.7.7: frames in a row without a skinned vertex buffer (gives up after NullVbMax)
            public bool Ok { get { return Layout >= 0 && Mask != null && R != null; } }
            // 0.8.0 v2 (design 1.5): the bake and its GPU buffers (null/false = this renderer stays on the v1 imprint)
            public ContactBake Bake;
            public GraphicsBuffer CanonBuf, AdjOffBuf, AdjVBuf, TissueBuf, ClassBuf, BendBuf, FieldA, FieldB, FieldC, FieldD, MarksBuf, GradBuf, Foreign;
            public bool V2;
            public int TanOff = -1;               // byte offset of the tangent (Float32x4) in the skinned buffer, -1 = none
            public float MarkUntil;               // marks keep fading until then (4 x MarkFade after the last pressure or slap)
            // her own limbs / the partner's body only (no hand near): Depth runs alone as a probe and counts pressed vertices (HitsBuf, read back);
            // the full chain runs only while that count was > 0 recently. A standing pose with her arms near her sides costs one dispatch.
            public GraphicsBuffer HitsBuf;
            public int SelfHits, HitFrame = -1000;
            public bool HitInflight, ClearMarks;
            public Action<AsyncGPUReadbackRequest> OnHits;
            // overlay meshes (areola caps, tattoo, vulva rim) lying on the body: each vertex follows the body vertex under it (ApplyMap)
            public RendState Parent;
            public GraphicsBuffer MapBuf, MapWBuf;   // 0.9.7 MapWBuf: per vertex the parent's share (overlays 1; skin seams 1 at the seam, 0 a cm in)
            public int Mapped;
            public bool Extra;                       // 0.9.7: her neck / face skin, not a soft-zone mesh (marks and small bony dents, no v1 imprint)
            public int Level;                        // dispatch pass: 0 no parent, then one more than its parent (a parent's Grad is ready first)
            // this frame's plan
            public bool Run, NeedCalib, IsV2, Imprint1, Depth, Bend, Wave, MarkOn, Decay, Work, Probe, SelfOnly, ChainRan, MapOnly;
            public int DiscMask, CapOff, CapN, CapN1;
        }

        static readonly int PVerts = Shader.PropertyToID("_Verts"), PStride = Shader.PropertyToID("_Stride"),
            PPosOff = Shader.PropertyToID("_PosOff"), PNrmOff = Shader.PropertyToID("_NrmOff"), PCount = Shader.PropertyToID("_Count"),
            PToWorld = Shader.PropertyToID("_ToWorld"), PToLocal = Shader.PropertyToID("_ToLocal"),
            PCaps = Shader.PropertyToID("_Caps"), PCapCount = Shader.PropertyToID("_CapCount"), PMask = Shader.PropertyToID("_Mask"),
            PLimit = Shader.PropertyToID("_Limit"), PMargin = Shader.PropertyToID("_Margin"), PSmoke = Shader.PropertyToID("_SmokeOffset"),
            PCapOff = Shader.PropertyToID("_CapOff"), PTanOff = Shader.PropertyToID("_TanOff"),
            PCanon = Shader.PropertyToID("_Canon"), PAdjOff = Shader.PropertyToID("_AdjOff"), PAdjV = Shader.PropertyToID("_AdjV"), PTissue = Shader.PropertyToID("_Tissue"),
            PClass = Shader.PropertyToID("_Class"), PBend = Shader.PropertyToID("_Bend"), PFieldIn = Shader.PropertyToID("_FieldIn"), PFieldOut = Shader.PropertyToID("_FieldOut"),
            PFieldN = Shader.PropertyToID("_FieldN"), PFieldW = Shader.PropertyToID("_FieldW"), PFieldR = Shader.PropertyToID("_FieldR"), PForeign = Shader.PropertyToID("_Foreign"), PForeignR = Shader.PropertyToID("_ForeignR"), PMapW = Shader.PropertyToID("_MapW"), PWaveImpact = Shader.PropertyToID("_WaveImpact"), PMarks = Shader.PropertyToID("_Marks"), PGrad = Shader.PropertyToID("_Grad"),
            PWaves = Shader.PropertyToID("_Waves"), PWaveCount = Shader.PropertyToID("_WaveCount"), PNoField = Shader.PropertyToID("_NoField"), PMarkOn = Shader.PropertyToID("_MarkOn"),
            PSoftScale = Shader.PropertyToID("_SoftScale"), PDepth = Shader.PropertyToID("_Depth"), PSkirt = Shader.PropertyToID("_SkirtLen"), PBulge = Shader.PropertyToID("_Bulge"),
            PBulgeCap = Shader.PropertyToID("_BulgeCap"), PNormalBend = Shader.PropertyToID("_NormalBend"), PDt = Shader.PropertyToID("_Dt"), PMarkGain = Shader.PropertyToID("_MarkGain"),
            PMarkFade = Shader.PropertyToID("_MarkFade"), PDentFade = Shader.PropertyToID("_DentFade"), PWaveAmt = Shader.PropertyToID("_WaveAmt"),
            PBendAmt0 = Shader.PropertyToID("_BendAmt0"), PBendAmt1 = Shader.PropertyToID("_BendAmt1"), PBendAmt2 = Shader.PropertyToID("_BendAmt2"),
            PDisc = Shader.PropertyToID("_Disc"), PDiscAmt = Shader.PropertyToID("_DiscAmt"),
            PHits = Shader.PropertyToID("_Hits"), PMap = Shader.PropertyToID("_Map"), PParentGrad = Shader.PropertyToID("_ParentGrad"),
            PParentOn = Shader.PropertyToID("_ParentOn"), POwnOn = Shader.PropertyToID("_OwnOn"), PCountHits = Shader.PropertyToID("_CountHits");
        static readonly uint[] ZeroHits = new uint[1];
        const int SelfHitHold = 30;               // frames a positive probe keeps the full chain on (readbacks arrive 1-3 frames late)

        static readonly string[] SpaceNames = { "pending", "rootBone", "transform", "identity", "rootBone (unscaled)" };

        bool _bundleHeld;                     // we hold a ShaderBundle reference
        ComputeShader _cs;
        int _kSmoke, _kImprint;
        int _kDepth, _kDilate, _kSmooth, _kCompose, _kGrad, _kApply, _kDisc, _kDecay, _kApplyMap;   // 0.8.0 v2 kernels
        bool _v2Kernels, _v2Failed, _loggedFirstV2;
        bool _foreignMarks;
        bool _seamBlend;                      // 0.9.7 bundle: ApplyMap blends by _MapW (skin seams), Compose reads _WaveImpact                   // 0.9.4 bundle: Depth writes the foreign-only press (_Foreign) that Compose takes marks from
        ComputeBuffer _capBuf, _waveBuf;
        readonly CapData[] _capArr = new CapData[CapBufCount];
        // the frame's world-space capsules (PrepareCaps) before they are copied per renderer
        readonly Vector3[] _wcA = new Vector3[MaxCaps], _wcB = new Vector3[MaxCaps], _wcMin = new Vector3[MaxCaps], _wcMax = new Vector3[MaxCaps];
        readonly float[] _wcR = new float[MaxCaps];
        readonly uint[] _wcMask = new uint[MaxCaps];
        readonly bool[] _wcV2Only = new bool[MaxCaps];
        readonly float[] _wcW = new float[MaxCaps];   // 0.9.4 mark weight (0 = her own limb)
        // 0.9.4: per foreign capsule its centre last frame and its smoothed speed relative to her pelvis (the mark weight)
        struct CapVel { public Vector3 P; public float Spd; public int Frame; }
        readonly Dictionary<long, CapVel> _capVel = new Dictionary<long, CapVel>();
        Vector3 _refP;
        int _refFrame = -10;
        [StructLayout(LayoutKind.Explicit)] struct FloatBits { [FieldOffset(0)] public float F; [FieldOffset(0)] public uint U; }
        int _selfN, _pbN;
        readonly Vector4[] _waveArr = new Vector4[2 * Ripple.MaxWaves], _discArr = new Vector4[4];
        readonly float[] _discAmt = new float[4];
        long _statSig = long.MinValue;
        Character _her;
        // 0.8.0: renderers that did contact work last frame, and whether any capsule reached one
        public int ActiveRenderers;
        public bool Contact;
        List<SoftCapsule> _capList;           // the caller's list; converted to world space inside the dispatch
        int _capFrame = -1000;
        Vector3[] _dv;                        // blendshape delta scratch for the calibration's CPU skin
        bool _inLoop;
        float _limit = 0.02f, _margin = 0.003f;
        ContactSettings _set = new ContactSettings();
        readonly List<RendState> _rs = new List<RendState>();
        SoftZones _z;
        int _syncVersion = int.MinValue;
        int _lastFrame = -1;
        bool _subscribed, _dead, _evSeen, _evWarned, _loggedFirstRun;
        int _waitFrames;
        float _smokeStart, _smokeDur, _smokeMetres;

        GpuContact() { }

        // ---- load ----

        public static GpuContact TryLoad(string dir)
        {
            LastStatus = "GPU contact: off: not loaded";
            try
            {
                bool cs = SystemInfo.supportsComputeShaders;
                Plugin.Log.LogInfo("GPU contact: supportsComputeShaders=" + cs + ", device " + SystemInfo.graphicsDeviceType + " " + SystemInfo.graphicsDeviceName);
                if (!cs) { Fail("no compute shaders"); return null; }
                string err;
                if (!ShaderBundle.Acquire(dir, out err)) { Fail("bundle (" + err + ")"); return null; }
                var g = new GpuContact();
                g._bundleHeld = true;
                g._cs = ShaderBundle.Load<ComputeShader>("SoftDeform");
                if (g._cs == null) { Fail("bundle (no SoftDeform compute shader inside)"); g.Dispose(); return null; }
                if (!g._cs.HasKernel("Smoke") || !g._cs.HasKernel("Imprint")) { Fail("kernel (Smoke/Imprint missing)"); g.Dispose(); return null; }
                g._kSmoke = g._cs.FindKernel("Smoke");
                g._kImprint = g._cs.FindKernel("Imprint");
                string miss = "";
                g._kDepth = Kernel(g._cs, "Depth", ref miss); g._kDilate = Kernel(g._cs, "Dilate", ref miss); g._kSmooth = Kernel(g._cs, "Smooth", ref miss);
                g._kCompose = Kernel(g._cs, "Compose", ref miss); g._kGrad = Kernel(g._cs, "Grad", ref miss); g._kApply = Kernel(g._cs, "Apply", ref miss);
                g._kDisc = Kernel(g._cs, "MarkDisc", ref miss); g._kDecay = Kernel(g._cs, "MarksDecay", ref miss); g._kApplyMap = Kernel(g._cs, "ApplyMap", ref miss);
                g._v2Kernels = miss.Length == 0;
                g._foreignMarks = g._cs.HasKernel("ForeignMarks");
                g._seamBlend = g._cs.HasKernel("SeamBlend");
                Plugin.Log.LogInfo("GPU contact: bundle loaded, kernels Smoke=" + g._kSmoke + " Imprint=" + g._kImprint + (g._v2Kernels ? ", v2: Depth Dilate Smooth Compose Grad Apply MarkDisc MarksDecay ApplyMap" : ""));
                if (!g._v2Kernels) Plugin.Log.LogWarning("GPU contact: bundle has no v2 kernels (old sfmorph_shaders?): v1 imprint (missing:" + miss + ")");
                else if (!g._seamBlend) Plugin.Log.LogWarning("GPU contact: old sfmorph_shaders (no SeamBlend): her skin meshes are not stitched at their seams, no neck / face marks, no ripple impact setting; install the bundle that came with this DLL");
                if (g._v2Kernels && !g._foreignMarks) Plugin.Log.LogWarning("GPU contact: old sfmorph_shaders (no ForeignMarks): marks still come from every contact, her own limbs too; install the bundle that came with this DLL");
                g._capBuf = new ComputeBuffer(CapBufCount, 48, ComputeBufferType.Structured);
                g._waveBuf = new ComputeBuffer(2 * Ripple.MaxWaves, 16, ComputeBufferType.Structured);
                RenderPipelineManager.beginContextRendering += g.OnBeginContext;
                g._subscribed = true;
                g._inLoop = InsertStep(g.OnLoopStep);
                Plugin.Log.LogInfo("GPU contact: PlayerLoop step " + (g._inLoop ? "inserted after UpdateAllSkinnedMeshes" : "not inserted (SRP hook only)"));
                g.UpdateStatus();
                LastStatus = g.Status;
                return g;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("GPU contact: load failed: " + e);
                Fail("bundle (exception)");
                return null;
            }
        }

        static int Kernel(ComputeShader cs, string name, ref string missing)
        {
            if (cs.HasKernel(name)) return cs.FindKernel(name);
            missing += " " + name;
            return -1;
        }

        static void Fail(string why)
        {
            LastStatus = "GPU contact: off: " + why;
            Plugin.Log.LogWarning(LastStatus);
        }

        // ---- sync with the soft zones' build ----

        public void Sync(SoftZones z, Character her)
        {
            try
            {
                if (_dead) { Teardown(); return; }
                if (z == null) { _z = null; _her = null; _syncVersion = int.MinValue; RemoveRenderers(null); UpdateStatus(); return; }
                if (z == _z && z.BuildVersion == _syncVersion) return;
                _z = z;
                _her = her;
                _syncVersion = z.BuildVersion;
                var keep = new List<SkinnedMeshRenderer>();
                var rends = z.GpuRenderers;
                if (rends != null)
                    for (int i = 0; i < rends.Count; i++)
                    {
                        var r = rends[i];
                        if (r == null) continue;
                        float[] mask = z.MaskFor(r);
                        if (mask == null) continue;
                        keep.Add(r);
                        var s = Find(r);
                        if (s == null) { s = new RendState { R = r, Name = r.name }; _rs.Add(s); EnableRaw(s); }
                        SetupMesh(s, mask, z.ZoneFor(r));
                    }
                // 0.9.7 (user: marks stop at her neck, the head gets none): her neck and face skin join the v2 chain (bony dents of a few mm, marks);
                // they are not soft-zone meshes, so no v1 imprint (zero mask) and they are dropped if their bake fails
                if (her != null && _v2Kernels && !_v2Failed && _seamBlend)
                    for (int i = 0; i < her.Renderers.Count; i++)
                    {
                        var r = her.Renderers[i];
                        if (r == null || !r.gameObject.activeInHierarchy || keep.Contains(r) || !SkinExtra(r)) continue;
                        var s = Find(r);
                        if (s == null) { s = new RendState { R = r, Name = r.name }; _rs.Add(s); EnableRaw(s); }
                        s.Extra = true;
                        SetupMesh(s, new float[r.sharedMesh.vertexCount], null);
                        if (s.V2) keep.Add(r);
                    }
                RemoveRenderers(keep);
                if (_v2Kernels && !_v2Failed) BuildMaps();
                if (_v2Kernels && !_v2Failed && her != null) BendPose.Bind(her);
                UpdateStatus();
                int baked = 0;
                for (int i = 0; i < _rs.Count; i++) if (_rs[i].V2) baked++;
                if (_v2Kernels && !_v2Failed)
                    Plugin.Log.LogInfo("GPU contact: v2 " + (baked > 0 ? "on (" + baked + " of " + _rs.Count + " renderers baked" + (_rs.Count > baked ? ", the rest on the v1 imprint" : "")
                                       + ", bend " + BendPose.Joints + " joints)" : "off for now: no renderer baked, v1 imprint"));
            }
            catch (Exception e) { Plugin.Log.LogError("GPU contact: sync failed, turned off: " + e); GoDead("error"); }
        }

        const float MapDist = 0.005f;   // an overlay vertex follows a body vertex within 5 mm of it (areola caps sit ~1 mm above the breast)
        const float SeamIn = 0.0015f, SeamOut = 0.0099f;   // 0.9.7 skin seams: the parent's move in full within 1.5 mm of its skin, faded out by 1 cm

        static bool IsBodyMesh(RendState s)
        {
            string n = s.Name ?? "";
            return n.IndexOf("body", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("uterus", StringComparison.OrdinalIgnoreCase) < 0;
        }

        // 0.9.7: her neck and face skin (Wrinkles' material classes face / neck), never nails, eyes, brows, lashes or tears
        static bool SkinExtra(SkinnedMeshRenderer r)
        {
            if (r.sharedMesh == null || r.sharedMesh.vertexCount <= 0) return false;
            string n = (r.name ?? "").ToLowerInvariant();
            if (n.Contains("nail") || n.Contains("tear") || n.Contains("eye") || n.Contains("brow") || n.Contains("lash")) return false;
            var mats = r.sharedMaterials;
            if (mats == null || mats.Length == 0 || mats[0] == null) return false;
            int c = Wrinkles.Classify(mats[0], r);
            return c == 1 || c == 2;
        }

        // Skin meshes in parent order (0.9.7): the upper body first, the other body meshes, the neck, the face. A skin mesh stitches its seam to
        // an earlier one; -1 = not a skin mesh (an overlay).
        static int SkinRank(RendState s)
        {
            if (s.Extra) return (s.Name ?? "").IndexOf("face", StringComparison.OrdinalIgnoreCase) >= 0 ? 3 : 2;
            if (!IsBodyMesh(s)) return -1;
            return (s.Name ?? "").IndexOf("upper", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1;
        }

        static long Cell(Vector3 p) { return CellKey(Mathf.FloorToInt(p.x * 100f), Mathf.FloorToInt(p.y * 100f), Mathf.FloorToInt(p.z * 100f)); }
        static long CellKey(int x, int y, int z) { return ((long)x * 73856093L) ^ ((long)y * 19349663L) ^ ((long)z * 83492791L); }

        // The bake's canonical vertices in 1 cm cells.
        static Dictionary<long, List<int>> Grid(ContactBake bk)
        {
            var g = new Dictionary<long, List<int>>();
            for (int v = 0; v < bk.V; v++)
            {
                if (bk.Canon[v] != (uint)v) continue;
                var p = bk.WorldP[v];
                if (float.IsNaN(p.x)) continue;
                long key = Cell(p);
                List<int> l;
                if (!g.TryGetValue(key, out l)) { l = new List<int>(4); g[key] = l; }
                l.Add(v);
            }
            return g;
        }

        // Nearest gridded vertex to p closer than sqrt(bd) (the 27 cells around p, so bd <= 1 cm^2); -1 none. bd is lowered to the hit.
        static int Nearest(Dictionary<long, List<int>> g, Vector3[] wp, Vector3 p, ref float bd)
        {
            int best = -1;
            int cx = Mathf.FloorToInt(p.x * 100f), cy = Mathf.FloorToInt(p.y * 100f), cz = Mathf.FloorToInt(p.z * 100f);
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
            {
                List<int> l;
                if (!g.TryGetValue(CellKey(cx + dx, cy + dy, cz + dz), out l)) continue;
                for (int k = 0; k < l.Count; k++)
                {
                    float d2 = (wp[l[k]] - p).sqrMagnitude;
                    if (d2 < bd) { bd = d2; best = l[k]; }
                }
            }
            return best;
        }

        // Maps, once per Sync (allocates then only), from the bakes' CPU-skinned positions (same pose):
        // - overlay meshes (every v2 renderer that isn't skin) onto the body: per vertex the nearest body canonical vertex within MapDist, weight 1,
        //   the parent = the body mesh most of its vertices found;
        // - 0.9.7 (user: cut-outs in her stomach when pressing it): her skin is several meshes (upper / lower body, neck, face) whose fields were
        //   computed apart (each mesh's skirt and smoothing see only its own side), so a press across a seam moved its two edges differently and
        //   opened it. A later skin mesh takes an earlier one as its parent (most vertices within SeamIn): within SeamIn of the parent's skin a
        //   vertex moves exactly like the parent's vertex there, fading to its own field by SeamOut.
        void BuildMaps()
        {
            var bodies = new List<RendState>();
            var skins = new List<RendState>();
            for (int i = 0; i < _rs.Count; i++)
            {
                var r = _rs[i];
                r.Parent = null; r.Mapped = 0; r.Level = 0; Rel(ref r.MapBuf); Rel(ref r.MapWBuf);
                if (!r.V2 || r.Bake == null || r.Bake.WorldP == null) continue;
                if (IsBodyMesh(r)) bodies.Add(r);
                if (SkinRank(r) >= 0) skins.Add(r);
            }
            if (skins.Count == 0) return;
            skins.Sort((a, b) => SkinRank(a) != SkinRank(b) ? SkinRank(a).CompareTo(SkinRank(b)) : b.Count.CompareTo(a.Count));
            var grids = new Dictionary<RendState, Dictionary<long, List<int>>>();
            Func<RendState, Dictionary<long, List<int>>> gridOf = r =>
            {
                Dictionary<long, List<int>> g;
                if (!grids.TryGetValue(r, out g)) { g = Grid(r.Bake); grids[r] = g; }
                return g;
            };
            var sb = new StringBuilder();
            var sbS = new StringBuilder();
            // skin seams: a parent comes earlier in `skins`, so its own map and level are known
            if (_seamBlend)
                for (int si = 1; si < skins.Count; si++)
                {
                    var r = skins[si];
                    var bk = r.Bake;
                    var best = new int[bk.V];
                    var dist = new float[bk.V];
                    var bestOf = new int[bk.V];
                    var votes = new int[si];
                    for (int v = 0; v < bk.V; v++)
                    {
                        best[v] = -1; bestOf[v] = -1; dist[v] = 1f;
                        var p = bk.WorldP[v];
                        if (float.IsNaN(p.x)) continue;
                        float bd = SeamOut * SeamOut;
                        for (int pi = 0; pi < si; pi++)
                        {
                            var pr = skins[pi];
                            if (SkinRank(pr) >= SkinRank(r) && !(SkinRank(pr) == 1 && SkinRank(r) == 1)) continue;
                            int k = Nearest(gridOf(pr), pr.Bake.WorldP, p, ref bd);
                            if (k >= 0) { best[v] = k; bestOf[v] = pi; }
                        }
                        dist[v] = Mathf.Sqrt(bd);
                        if (bestOf[v] >= 0 && dist[v] <= SeamIn) votes[bestOf[v]]++;
                    }
                    int pb = -1;
                    for (int b = 0; b < si; b++) if (votes[b] > 0 && (pb < 0 || votes[b] > votes[pb])) pb = b;
                    if (pb < 0 || votes[pb] < 4) continue;
                    var map = new int[bk.V];
                    var mw = new float[bk.V];
                    int mapped = 0, exact = 0;
                    for (int v = 0; v < bk.V; v++)
                    {
                        map[v] = -1;
                        if (bestOf[v] != pb) continue;
                        float w = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(SeamIn, SeamOut, dist[v]));
                        if (w <= 0.001f) continue;
                        map[v] = best[v]; mw[v] = w >= 0.999f ? 1f : w; mapped++;
                        if (w >= 0.999f) exact++;
                    }
                    try { r.MapBuf = Upload(map, bk.V, 4); r.MapWBuf = Upload(mw, bk.V, 4); }
                    catch (Exception e) { Plugin.Log.LogWarning("GPU contact: seam map of " + r.Name + " failed: " + e.Message); Rel(ref r.MapBuf); Rel(ref r.MapWBuf); continue; }
                    r.Parent = skins[pb]; r.Mapped = mapped; r.Level = skins[pb].Level + 1;
                    sbS.Append(sbS.Length > 0 ? ", " : "").Append(r.Name).Append(" -> ").Append(skins[pb].Name).Append(' ').Append(exact).Append(" on the seam + ")
                        .Append(mapped - exact).Append(" blended");
                }
            // overlays onto the body meshes
            for (int i = 0; i < _rs.Count; i++)
            {
                var r = _rs[i];
                if (!r.V2 || r.Bake == null || r.Bake.WorldP == null || SkinRank(r) >= 0 || bodies.Count == 0) continue;
                var bk = r.Bake;
                var best = new int[bk.V];
                var bestBody = new int[bk.V];
                var votes = new int[bodies.Count];
                for (int v = 0; v < bk.V; v++)
                {
                    best[v] = -1; bestBody[v] = -1;
                    var p = bk.WorldP[v];
                    if (float.IsNaN(p.x)) continue;
                    float bd = MapDist * MapDist;
                    for (int b = 0; b < bodies.Count; b++)
                    {
                        int k = Nearest(gridOf(bodies[b]), bodies[b].Bake.WorldP, p, ref bd);
                        if (k >= 0) { best[v] = k; bestBody[v] = b; }
                    }
                    if (bestBody[v] >= 0) votes[bestBody[v]]++;
                }
                int pb = 0;
                for (int b = 1; b < bodies.Count; b++) if (votes[b] > votes[pb]) pb = b;
                if (votes[pb] < 8) continue;
                var map = new int[bk.V];
                var mw = new float[bk.V];
                int mapped = 0;
                for (int v = 0; v < bk.V; v++) { map[v] = bestBody[v] == pb ? best[v] : -1; mw[v] = 1f; if (map[v] >= 0) mapped++; }
                try { r.MapBuf = Upload(map, bk.V, 4); r.MapWBuf = Upload(mw, bk.V, 4); }
                catch (Exception e) { Plugin.Log.LogWarning("GPU contact: overlay map of " + r.Name + " failed: " + e.Message); Rel(ref r.MapBuf); Rel(ref r.MapWBuf); continue; }
                r.Parent = bodies[pb]; r.Mapped = mapped; r.Level = bodies[pb].Level + 1;
                sb.Append(sb.Length > 0 ? ", " : "").Append(r.Name).Append(" -> ").Append(bodies[pb].Name).Append(' ').Append(mapped).Append('/').Append(bk.V);
            }
            if (sbS.Length > 0) Plugin.Log.LogInfo("GPU contact: skin seams stitched: " + sbS);
            if (sb.Length > 0) Plugin.Log.LogInfo("GPU contact: overlays follow the body under them: " + sb);
        }

        RendState Find(SkinnedMeshRenderer r)
        {
            for (int i = 0; i < _rs.Count; i++) if (_rs[i].R == r) return _rs[i];
            return null;
        }

        static void EnableRaw(RendState s)
        {
            if (s.RawHeld) return;
            RawTarget.Acquire(s.R);   // ref-counted: Unity recreates the skinned buffers once; the flag only comes off if nobody set it before
            s.RawHeld = true;
        }

        static void RestoreRaw(RendState s)
        {
            if (!s.RawHeld) return;
            s.RawHeld = false;
            RawTarget.Release(s.R);
        }

        // Mesh-dependent state: layout from the mesh, the mask buffer, the v2 bake and its buffers. The skinned-buffer check (stride/count) follows on the first frame.
        void SetupMesh(RendState s, float[] mask, float[] zone)
        {
            var m = s.R.sharedMesh;
            s.Mesh = m;
            s.Layout = 0;
            ReleaseV2(s);
            if (s.Mask != null) { s.Mask.Release(); s.Mask = null; }
            if (m == null) { s.Layout = -1; return; }
            s.Count = m.vertexCount;
            string why = ReadLayout(m, out s.Stride, out s.PosOff, out s.NrmOff);
            if (why == null && (mask == null || mask.Length != s.Count)) why = "mask length " + (mask == null ? 0 : mask.Length) + " != vertex count " + s.Count;
            if (why != null)
            {
                s.Layout = -1;
                Plugin.Log.LogWarning("GPU contact: renderer " + s.Name + " mesh " + m.name + " skipped: " + why);
                return;
            }
            s.Mask = new GraphicsBuffer(GraphicsBuffer.Target.Structured, s.Count, 4);
            s.Mask.SetData(mask);
            // tangent Float32x4 on stream 0 (the skinned buffer has it at the mesh's offset): the v2 Apply keeps it orthogonal to the bent normal
            s.TanOff = -1;
            if (m.HasVertexAttribute(VertexAttribute.Tangent) && m.GetVertexAttributeStream(VertexAttribute.Tangent) == 0
                && m.GetVertexAttributeFormat(VertexAttribute.Tangent) == VertexAttributeFormat.Float32 && m.GetVertexAttributeDimension(VertexAttribute.Tangent) == 4)
                s.TanOff = m.GetVertexAttributeOffset(VertexAttribute.Tangent);
            Plugin.Log.LogInfo("GPU contact: renderer " + s.Name + " mesh " + m.name + " count " + s.Count + " stride " + s.Stride + " posOff " + s.PosOff + " nrmOff " + s.NrmOff
                + " tanOff " + s.TanOff + " (Raw set by us: " + RawTarget.SetByUs(s.R) + ")" + (s.Space > 0 ? ", space " + SpaceNames[s.Space] + " (kept)" : ""));
            if (_v2Kernels && !_v2Failed) MakeBake(s, zone);
        }

        static GraphicsBuffer Upload(Array data, int count, int stride)
        {
            var b = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(1, count), stride);
            if (data != null && count > 0) b.SetData(data);
            return b;
        }

        // The v2 bake of one renderer and its GPU buffers; a failure leaves the renderer on the v1 imprint (said once per build).
        void MakeBake(RendState s, float[] zone)
        {
            string err;
            ContactBake b = ContactBake.Build(s.R, zone, _her, out err);
            if (b == null || b.V != s.Count)
            {
                Plugin.Log.LogWarning("GPU contact: renderer " + s.Name + ": v2 bake failed (" + (b == null ? err : "vertex count " + b.V + " != " + s.Count) + "): v1 imprint for it");
                return;
            }
            try
            {
                int V = s.Count;
                s.CanonBuf = Upload(b.Canon, V, 4);
                s.AdjOffBuf = Upload(b.AdjOff, V + 1, 4);
                s.AdjVBuf = Upload(b.AdjV, b.AdjV.Length, 4);
                s.TissueBuf = Upload(b.Tissue, V, 8);
                s.ClassBuf = Upload(b.Class, V, 4);
                s.BendBuf = Upload(b.Bend, V, 16);
                var zeroF = new float[V];
                s.FieldA = Upload(zeroF, V, 4); s.FieldB = Upload(zeroF, V, 4); s.FieldC = Upload(zeroF, V, 4); s.FieldD = Upload(zeroF, V, 4);
                s.Foreign = Upload(new Vector2[V], V, 8);
                s.MarksBuf = Upload(new Vector4[V], V, 16);
                s.GradBuf = Upload(new Vector4[2 * V], 2 * V, 16);
                s.HitsBuf = Upload(ZeroHits, 1, 4);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("GPU contact: renderer " + s.Name + ": v2 buffers failed (" + e.Message + "): v1 imprint for it");
                ReleaseV2(s);
                return;
            }
            s.Bake = b;
            s.V2 = true;
            s.MarkUntil = 0f;
            s.SelfHits = 0; s.HitFrame = -1000; s.HitInflight = false;
            if (s.OnHits == null)
            {
                var st = s;
                st.OnHits = req =>
                {
                    st.HitInflight = false;
                    if (!st.Alive || req.hasError || st.HitsBuf == null) return;
                    try { var d = req.GetData<uint>(); st.SelfHits = d.Length > 0 ? (int)d[0] : 0; st.HitFrame = Time.frameCount; }
                    catch (Exception) { }
                };
            }
            BakeVersion++;
            Plugin.Log.LogInfo("GPU contact: baked " + s.Name + ": " + b.Note);
        }

        static void Rel(ref GraphicsBuffer b) { if (b != null) { b.Release(); b = null; } }

        // Frees a renderer's v2 buffers and bake (bumps BakeVersion when there was a bake).
        void ReleaseV2(RendState s)
        {
            bool had = s.Bake != null;
            Rel(ref s.CanonBuf); Rel(ref s.AdjOffBuf); Rel(ref s.AdjVBuf); Rel(ref s.TissueBuf); Rel(ref s.ClassBuf); Rel(ref s.BendBuf);
            Rel(ref s.FieldA); Rel(ref s.FieldB); Rel(ref s.FieldC); Rel(ref s.FieldD); Rel(ref s.Foreign); Rel(ref s.MarksBuf); Rel(ref s.GradBuf);
            Rel(ref s.HitsBuf); Rel(ref s.MapBuf); Rel(ref s.MapWBuf);
            s.Parent = null; s.Mapped = 0; s.Level = 0;
            for (int i = 0; i < _rs.Count; i++) if (_rs[i].Parent == s) { _rs[i].Parent = null; Rel(ref _rs[i].MapBuf); Rel(ref _rs[i].MapWBuf); _rs[i].Mapped = 0; _rs[i].Level = 0; }
            s.Bake = null;
            s.V2 = false;
            if (had) BakeVersion++;
        }

        // Position and Normal Float32x3 on stream 0 (the layout the shader assumes); null = fine, else the reason.
        static string ReadLayout(Mesh m, out int stride, out int posOff, out int nrmOff)
        {
            stride = posOff = nrmOff = 0;
            if (!m.HasVertexAttribute(VertexAttribute.Position) || !m.HasVertexAttribute(VertexAttribute.Normal)) return "no position/normal";
            string w = Check(m, VertexAttribute.Position);
            if (w == null) w = Check(m, VertexAttribute.Normal);
            if (w != null) return w;
            stride = m.GetVertexBufferStride(0);
            posOff = m.GetVertexAttributeOffset(VertexAttribute.Position);
            nrmOff = m.GetVertexAttributeOffset(VertexAttribute.Normal);
            return null;
        }

        static string Check(Mesh m, VertexAttribute a)
        {
            if (m.GetVertexAttributeStream(a) != 0) return a + " not on stream 0";
            if (m.GetVertexAttributeFormat(a) != VertexAttributeFormat.Float32 || m.GetVertexAttributeDimension(a) != 3) return a + " is not Float32x3";
            return null;
        }

        // Drops (and restores) every renderer not in keep; keep == null drops all.
        void RemoveRenderers(List<SkinnedMeshRenderer> keep)
        {
            for (int i = _rs.Count - 1; i >= 0; i--)
            {
                var s = _rs[i];
                if (keep != null && keep.Contains(s.R)) continue;
                DropState(s);
                _rs.RemoveAt(i);
            }
        }

        void DropState(RendState s)
        {
            s.Alive = false;
            RestoreRaw(s);
            ReleaseV2(s);
            if (s.Mask != null) { s.Mask.Release(); s.Mask = null; }
        }

        // ---- per frame inputs ----

        // Stores the caller's list; the world-space conversion and the upload happen inside the dispatch (the hook / loop step).
        // limit: v1's deepest press (m); v2 takes its caps from the bake and s (design 1.3).
        public void SetCapsules(List<SoftCapsule> caps, float limit, float margin, ContactSettings s)
        {
            try
            {
                if (_dead) { Teardown(); return; }
                _limit = limit; _margin = margin;
                _set = s;
                _capList = caps;
                _capFrame = Time.frameCount;
                if (!_evSeen && !_evWarned && _rs.Count > 0 && ++_waitFrames > 600)
                {
                    _evWarned = true;
                    Plugin.Log.LogWarning("GPU contact: neither the PlayerLoop step nor beginContextRendering has fired in 600 frames with renderers synced; nothing will be deformed");
                }
            }
            catch (Exception e) { Plugin.Log.LogError("GPU contact: SetCapsules failed, turned off: " + e); GoDead("error"); }
        }

        // ---- the capsules of a frame ----

        // World-space capsules for this frame (T == null and the capsules of hidden sources (0.7.7) skipped, at most MaxCaps), with their AABBs and,
        // for her own limbs and the partner's body (v2 only), the skip mask: bit k set = a vertex of bone class k is NOT pressed by this capsule. Her
        // own capsules count only with [Physics] SelfContact and are inset at both ends by radius + 1 cm (a limb's joint ends overlap its neighbours),
        // the partner's body only with PartnerBody. Returns the count.
        int PrepareCaps()
        {
            int n = 0, self = 0, pb = 0;
            var caps = _capList;
            // 0.9.4 marks: a foreign capsule's mark weight grows with its speed relative to her pelvis (a hand resting on her moves with her)
            int fr = Time.frameCount;
            float dt = Mathf.Max(1e-3f, Time.deltaTime);
            Transform root = null;
            if (_her != null) _her.Bones.TryGetValue("Root_M", out root);
            Vector3 refV = Vector3.zero;
            if (root != null)
            {
                Vector3 rp = root.position;
                if (fr - _refFrame == 1) { refV = (rp - _refP) / dt; if (refV.sqrMagnitude > 36f) refV = Vector3.zero; }   // a teleport is not motion
                _refP = rp; _refFrame = fr;
            }
            if (_capVel.Count > 600) _capVel.Clear();
            if (caps != null)
                for (int i = 0; i < caps.Count && n < MaxCaps; i++)
                {
                    var c = caps[i];
                    if (c.T == null) continue;
                    if (c.Src != null && !c.Src.Live) continue;
                    string kind = c.Kind;
                    bool isSelf = kind == "thighL" || kind == "thighR" || kind == "forearm" || kind == "hand" || kind == "upperarm" || kind == "calf";
                    bool isBody = kind == "partnerBody";
                    if (isSelf && !_set.SelfContact) continue;
                    if (isBody && !_set.PartnerBody) continue;
                    Vector3 a = c.T.TransformPoint(c.LocalA), b = c.T.TransformPoint(c.LocalB);
                    uint mask = 0;
                    if (isSelf)
                    {
                        Vector3 ab = b - a;
                        float len = ab.magnitude, inset = c.Radius + 0.01f;
                        if (len <= 2f * inset) continue;
                        Vector3 d = ab / len;
                        a += d * inset; b -= d * inset;
                        mask = SelfMask(kind, NameOf(c.T));
                        if (mask == 0u) continue;
                        self++;
                    }
                    else if (isBody) pb++;
                    float w = 0f;
                    if (!isSelf)
                    {
                        long key = ((long)c.T.GetInstanceID() << 32) | (uint)c.LocalA.GetHashCode();
                        Vector3 mid = (a + b) * 0.5f;
                        CapVel cv;
                        float spd = 0f;
                        if (_capVel.TryGetValue(key, out cv) && fr - cv.Frame == 1)
                        {
                            float sp = ((mid - cv.P) / dt - refV).magnitude;
                            if (sp > 6f) sp = cv.Spd;   // a jump (the rig moved), not a hand
                            spd = cv.Spd + (sp - cv.Spd) * Mathf.Clamp01(dt / 0.08f);
                        }
                        else if (cv.Frame == fr) spd = cv.Spd;
                        _capVel[key] = new CapVel { P = mid, Spd = spd, Frame = fr };
                        w = Mathf.Clamp(0.4f + 1.5f * spd, 0.4f, 1.5f);   // resting 0.4 (only a firm squeeze marks, slowly), 0.4 m/s 1.0, from 0.73 m/s 1.5
                    }
                    // her own hand: the capsule (22 mm) is about twice a palm's half-thickness; a hand hanging at her side would dimple the thigh by a cm
                    float rad = kind == "hand" ? c.Radius * 0.6f : c.Radius;
                    _wcA[n] = a; _wcB[n] = b; _wcR[n] = rad; _wcMask[n] = mask; _wcV2Only[n] = isSelf || isBody; _wcW[n] = w;
                    float ext = rad + _margin;
                    _wcMin[n] = new Vector3(Mathf.Min(a.x, b.x) - ext, Mathf.Min(a.y, b.y) - ext, Mathf.Min(a.z, b.z) - ext);
                    _wcMax[n] = new Vector3(Mathf.Max(a.x, b.x) + ext, Mathf.Max(a.y, b.y) + ext, Mathf.Max(a.z, b.z) + ext);
                    n++;
                }
            _selfN = self; _pbN = pb;
            return n;
        }

        // The vertex classes (ContactBake class ids) one of her own capsules never presses, on its side (from the capsule bone's _L/_R name, thighs
        // also from the kind): thigh -> thigh, butt, pelvis, genital; calf -> calf, foot; upper arm -> upper arm, scapula, forearm;
        // forearm -> forearm, hand, upper arm; hand -> hand, forearm. 0 = unknown side (the capsule is left out).
        // 0.9.9: Transform.name allocates a new string per call; this runs per self capsule every frame. Bone names don't change.
        readonly Dictionary<Transform, string> _boneNames = new Dictionary<Transform, string>();
        string NameOf(Transform t)
        {
            string n;
            if (!_boneNames.TryGetValue(t, out n)) { n = t.name; _boneNames[t] = n; }
            return n;
        }

        static uint SelfMask(string kind, string boneName)
        {
            int s = kind == "thighL" ? 0 : kind == "thighR" ? 1 : ContactBake.SideOf(boneName);
            if (s < 0) return 0u;
            switch (kind)
            {
                case "thighL": case "thighR":
                    return (1u << (ContactBake.ClsThigh + s)) | (1u << (ContactBake.ClsButt + s)) | (1u << ContactBake.ClsPelvis) | (1u << ContactBake.ClsGenital);
                case "calf": return (1u << (ContactBake.ClsCalf + s)) | (1u << (ContactBake.ClsFoot + s));
                case "upperarm": return (1u << (ContactBake.ClsUpperArm + s)) | (1u << ContactBake.ClsScapula) | (1u << (ContactBake.ClsForearm + s));
                case "forearm": return (1u << (ContactBake.ClsForearm + s)) | (1u << (ContactBake.ClsHand + s)) | (1u << (ContactBake.ClsUpperArm + s));
                case "hand": return (1u << (ContactBake.ClsHand + s)) | (1u << (ContactBake.ClsForearm + s));
            }
            return 0u;
        }

        // Copies the capsules that reach [bmin, bmax] into the upload array at `at`: first the ones every deformer takes (hands, partner), then
        // (v2) her own limbs and the partner's body. Returns the number of the first group in n1 and the total.
        int GatherCaps(int at, Vector3 bmin, Vector3 bmax, int ncap, bool v2, out int n1)
        {
            int n = 0;
            n1 = 0;
            for (int pass = 0; pass < (v2 ? 2 : 1); pass++)
            {
                for (int k = 0; k < ncap && n < MaxCaps && at + n < CapBufCount; k++)
                {
                    if (_wcV2Only[k] != (pass == 1)) continue;
                    if (_wcMax[k].x < bmin.x || _wcMin[k].x > bmax.x || _wcMax[k].y < bmin.y || _wcMin[k].y > bmax.y || _wcMax[k].z < bmin.z || _wcMin[k].z > bmax.z) continue;
                    int o = at + n;
                    _capArr[o].A = new Vector4(_wcA[k].x, _wcA[k].y, _wcA[k].z, _wcR[k]);
                    _capArr[o].B = new Vector4(_wcB[k].x, _wcB[k].y, _wcB[k].z, 0f);
                    _capArr[o].C0 = _wcMask[k]; _capArr[o].C1 = new FloatBits { F = _wcW[k] }.U; _capArr[o].C2 = 0u; _capArr[o].C3 = 0u;
                    n++;
                }
                if (pass == 0) n1 = n;
            }
            return n;
        }

        public void Smoke(float seconds, float metres)
        {
            _smokeStart = Time.realtimeSinceStartup;
            _smokeDur = Mathf.Max(0.1f, seconds);
            _smokeMetres = metres;
            Plugin.Log.LogInfo("GPU contact: smoke test " + _smokeDur.ToString("0.0") + " s, " + (metres * 1000f).ToString("0.#") + " mm");
        }

        // ---- the render hook ----

        // Only a context that renders the game's own (VR / main) camera dispatches; shadow, reflection-probe or preview-only contexts
        // return without touching the frame guard.
        void OnBeginContext(ScriptableRenderContext ctx, List<Camera> cams)
        {
            try
            {
                if (_dead || _cs == null) return;
                if (!HasGameCamera(cams)) return;
                Run();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("GPU contact: render callback failed, turned off: " + e);
                GoDead("error");
            }
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

        // PlayerLoop fallback: right after the skinned meshes are updated.
        void OnLoopStep()
        {
            try
            {
                if (_dead || _cs == null) return;
                Run();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("GPU contact: loop step failed, turned off: " + e);
                GoDead("error");
            }
        }

        // 0.8.0 (SkinMarks): her renderers whose v2 bake is live, and per renderer its bake, welded-id buffer (uint per vertex) and per-vertex
        // marks buffer (float4 per vertex, indexed by canonical id: x redness, y live pressure, z lingering dent m). False when r has no v2 state.
        public void MarkRenderers(List<SkinnedMeshRenderer> into)
        {
            into.Clear();
            for (int i = 0; i < _rs.Count; i++)
                if (_rs[i].V2 && _rs[i].Bake != null && _rs[i].R != null) into.Add(_rs[i].R);
        }

        public bool MarksOf(SkinnedMeshRenderer r, out ContactBake bake, out GraphicsBuffer canon, out GraphicsBuffer marks)
        {
            bake = null; canon = null; marks = null;
            var s = Find(r);
            if (s == null || !s.V2 || s.Bake == null || s.CanonBuf == null || s.MarksBuf == null) return false;
            bake = s.Bake; canon = s.CanonBuf; marks = s.MarksBuf;
            return true;
        }

        // The dispatch itself; called by the loop step and by the SRP hook, whichever comes first in a frame wins (shared frame guard).
        void Run()
        {
            int frame = Time.frameCount;
            if (frame == _lastFrame) return;   // Imprint is not idempotent: the first caller of the frame wins
            RunChains(frame);
            Perf.End(Perf.S.GpuRun);
            var after = AfterImprint;
            if (after != null)
            {
                Perf.Begin(Perf.S.MarksRaster);   // the skin marks' raster is the only AfterImprint handler
                try { after(frame); }
                catch (Exception e) { Plugin.Log.LogWarning("GPU contact: AfterImprint handler failed: " + e.Message); }
                Perf.End(Perf.S.MarksRaster);
            }
        }

        void RunChains(int frame)
        {
            _lastFrame = frame;
            _evSeen = true;
            // before anything of ours writes the skinned buffers: the wrinkle strain must read the clean skinning result
            var before = BeforeImprint;
            if (before != null)
                try { before(frame); }
                catch (Exception e) { Plugin.Log.LogWarning("GPU contact: BeforeImprint handler failed: " + e.Message); }
            Perf.Begin(Perf.S.GpuRun);   // after the wrinkle strain (timed on its own), ended in Run
            if (_rs.Count == 0) return;
            float cpu0 = Time.realtimeSinceStartup;
            float now = Time.time;
            float smokeT = _smokeDur > 0f ? (cpu0 - _smokeStart) / _smokeDur : 2f;
            float smoke = smokeT >= 0f && smokeT < 1f ? _smokeMetres * Mathf.Sin(Mathf.PI * smokeT) : 0f;
            float dt = Mathf.Clamp(Time.deltaTime, 0f, 0.1f);

            // v2 inputs of the frame: bend amplitudes, waves, slap discs (any failure drops back to the v1 imprint for good)
            bool useV2 = _v2Kernels && !_v2Failed;
            int nWaves = 0, nDisc = 0;
            if (useV2)
            {
                bool anyV2 = false;
                for (int i = 0; i < _rs.Count && !anyV2; i++) anyV2 = _rs[i].V2;
                if (anyV2)
                    try
                    {
                        BendPose.Update(_set);
                        nWaves = Mathf.Clamp(Ripple.Fill(now, _waveArr), 0, Ripple.MaxWaves);
                        if (nWaves > 0) _waveBuf.SetData(_waveArr, 0, 0, 2 * nWaves);
                        nDisc = Mathf.Clamp(SkinMarks.TakeDiscs(_discArr, _discAmt), 0, 4);
                        if (!_set.Marks) nDisc = 0;
                    }
                    catch (Exception e) { V2Fail("inputs", e); useV2 = false; nWaves = nDisc = 0; }
                else useV2 = false;
            }
            float softScale = Tissue.SoftScale(_set.Softness), depthMul = Mathf.Max(0.1f, _set.Depth);

            int ncap = _capList != null && frame - _capFrame <= 3 ? PrepareCaps() : 0;
            long sig = ((((long)_rs.Count * 31 + (useV2 ? 1 : 0)) * 31 + _selfN) * 31 + _pbN) * 31 + BendPose.Joints;
            sig = ((sig * 1009 + Mathf.RoundToInt(_set.Skirt * 1000f)) * 1009 + Mathf.RoundToInt(_set.Bulge * 100f)) * 3 + (_set.Normals ? 1 : 0) + (_set.SelfContact ? 7 : 0);
            if (sig != _statSig) { _statSig = sig; UpdateStatus(); }

            // plan: who has work this frame, and which capsules reach whom (all renderers' capsules go up in one upload)
            int capUsed = 0, active = 0;
            bool anyContact = false;
            for (int i = 0; i < _rs.Count; i++)
            {
                var s = _rs[i];
                s.Run = false;
                if (s.R == null || !s.R.enabled || !s.R.gameObject.activeInHierarchy) continue;
                if (s.Mesh != s.R.sharedMesh && _z != null) { if (s.Extra) SetupMesh(s, new float[s.R.sharedMesh != null ? s.R.sharedMesh.vertexCount : 0], null); else SetupMesh(s, _z.MaskFor(s.R), _z.ZoneFor(s.R)); }   // the mesh was swapped under us
                if (!s.Ok) continue;
                s.NeedCalib = s.Space == 0 && s.CalibState == 0 && s.CalibTries < CalibMaxTries + 4 && frame >= s.CalibNextFrame;   // +4: an unreadable mesh waits for a sibling's space (RequestCalibration)
                bool v2 = useV2 && s.V2 && s.Space > 0;
                s.IsV2 = v2;
                s.CapOff = capUsed; s.CapN = 0; s.CapN1 = 0;
                Bounds b = default(Bounds);
                if ((ncap > 0 && s.Space > 0) || (v2 && (nWaves > 0 || nDisc > 0)))
                {
                    b = s.R.bounds;
                    b.Expand(0.3f);   // 15 cm all round: a skinned renderer's bounds follow its root bone and can lag the posed limbs
                }
                if (ncap > 0 && s.Space > 0)
                {
                    int n1;
                    s.CapN = GatherCaps(capUsed, b.min, b.max, ncap, v2, out n1);
                    s.CapN1 = n1;
                    capUsed += s.CapN;
                }
                s.Imprint1 = !v2 && s.Space > 0 && s.CapN1 > 0;
                // only her own limbs / the partner's body in reach: probe (Depth alone, counting pressed vertices) until a probe found some
                s.SelfOnly = v2 && s.CapN > 0 && s.CapN1 == 0;
                s.Depth = v2 && s.CapN > 0 && (!s.SelfOnly || (s.SelfHits > 0 && frame - s.HitFrame <= SelfHitHold));
                s.Probe = s.SelfOnly && !s.Depth;
                s.Bend = v2 && BendPose.Any && s.Bake.BendCount > 0;
                s.Wave = v2 && nWaves > 0 && WaveReaches(b, nWaves);
                s.DiscMask = 0;
                if (v2 && nDisc > 0)
                    for (int d = 0; d < nDisc; d++)
                    {
                        float rr = _discArr[d].w + 0.02f;
                        if (b.SqrDistance(new Vector3(_discArr[d].x, _discArr[d].y, _discArr[d].z)) <= rr * rr) s.DiscMask |= 1 << d;
                    }
                if (v2 && _set.Marks && (s.Depth || s.DiscMask != 0)) s.MarkUntil = now + 4f * Mathf.Max(3f, _set.MarkFade);   // pressure or a slap: fade for 4 time constants
                // marks switched off: wipe what is left once, so switching them on again doesn't bring back an old print
                bool work = s.Depth || s.Bend || s.Wave || s.DiscMask != 0;
                s.ClearMarks = v2 && !_set.Marks && s.MarkUntil > 0f && !work;   // on the first frame without other work; MarkUntil cleared once it dispatched
                s.MarkOn = v2 && _set.Marks && now < s.MarkUntil;
                s.Decay = v2 && !work && (s.MarkOn || s.ClearMarks);
                s.Work = work;
                s.MapOnly = false;
                if (s.Depth || s.Imprint1) anyContact = true;
                s.Run = s.NeedCalib || s.Imprint1 || smoke != 0f || work || s.Decay || s.Probe;
                if (s.Run && (s.Imprint1 || work || s.Decay)) active++;
            }
            // an overlay whose body moves this frame runs at least its ApplyMap (it follows the body even with no work of its own)
            for (int i = 0; i < _rs.Count; i++)
            {
                var s = _rs[i];
                var par = s.Parent;
                if (par == null || !s.IsV2 || s.Work) continue;
                if (par.IsV2 && par.Work && par.Run && s.R != null && s.R.enabled && s.R.gameObject.activeInHierarchy && s.Ok) { s.MapOnly = true; s.Run = true; }
            }
            if (capUsed > 0) _capBuf.SetData(_capArr, 0, 0, capUsed);
            ActiveRenderers = active;
            Contact = anyContact;

            int dispatches = 0, v2Count = 0;
            for (int i = 0; i < _rs.Count; i++) _rs[i].ChainRan = false;
            int levels = 0;
            for (int i = 0; i < _rs.Count; i++) if (_rs[i].Parent != null && _rs[i].Level > levels) levels = _rs[i].Level;
            for (int pass = 0; pass <= levels; pass++)   // parents first: an overlay's / a seam's ApplyMap reads its parent's Grad of this frame
            for (int i = 0; i < _rs.Count; i++)
            {
                var s = _rs[i];
                if (!s.Run || (s.Parent != null ? s.Level : 0) != pass) continue;
                using (var vb = s.R.GetVertexBuffer())
                {
                    if (vb == null)
                    {
                        // a few frames without one are normal after the Raw target is set; for good it means no GPU skinning here: say so once, stop
                        if (++s.NullVb == NullVbMax)
                        {
                            Plugin.Log.LogWarning("GPU contact: '" + s.Name + "' has had no skinned vertex buffer for " + NullVbMax + " frames (GPU skinning off for it?); skipped");
                            s.Layout = -1;
                        }
                        continue;
                    }
                    s.NullVb = 0;
                    if (s.Layout == 0 && !CheckBuffer(s, vb)) continue;
                    if (s.Layout < 0) continue;
                    if (s.NeedCalib) { RequestCalibration(s, vb); continue; }   // read the untouched skinning result first
                    int groups = (s.Count + 63) / 64;
                    if (s.Imprint1)
                    {
                        Matrix4x4 toWorld = ToWorld(s);
                        _cs.SetBuffer(_kImprint, PVerts, vb);
                        _cs.SetBuffer(_kImprint, PMask, s.Mask);
                        _cs.SetBuffer(_kImprint, PCaps, _capBuf);
                        _cs.SetInt(PStride, s.Stride); _cs.SetInt(PPosOff, s.PosOff); _cs.SetInt(PNrmOff, s.NrmOff); _cs.SetInt(PCount, s.Count);
                        _cs.SetInt(PCapCount, s.CapN1); _cs.SetInt(PCapOff, s.CapOff);
                        _cs.SetMatrix(PToWorld, toWorld); _cs.SetMatrix(PToLocal, toWorld.inverse);
                        _cs.SetFloat(PLimit, _limit); _cs.SetFloat(PMargin, _margin);
                        _cs.Dispatch(_kImprint, groups, 1, 1);
                        dispatches++;
                    }
                    if (s.IsV2 && (s.Work || s.Decay || s.Probe || s.MapOnly))
                    {
                        try { dispatches += RunV2(s, vb, nWaves, dt, softScale, depthMul); v2Count++; }
                        catch (Exception e) { V2Fail("chain on " + s.Name, e); }
                    }
                    if (smoke != 0f)
                    {
                        _cs.SetBuffer(_kSmoke, PVerts, vb);
                        _cs.SetInt(PStride, s.Stride); _cs.SetInt(PPosOff, s.PosOff); _cs.SetInt(PNrmOff, s.NrmOff); _cs.SetInt(PCount, s.Count);
                        _cs.SetFloat(PSmoke, smoke);
                        _cs.Dispatch(_kSmoke, groups, 1, 1);
                        dispatches++;
                    }
                    if (!_loggedFirstRun) { _loggedFirstRun = true; Plugin.Log.LogInfo("GPU contact: first dispatch on " + s.Name + " (" + (s.Imprint1 ? "imprint " : "") + (s.IsV2 ? "v2 " : "") + (smoke != 0f ? "smoke" : "") + ")"); }
                }
            }
            if (v2Count > 0 && !_loggedFirstV2)
            {
                _loggedFirstV2 = true;
                Plugin.Log.LogInfo("GPU contact: first v2 frame: " + v2Count + " renderers, " + dispatches + " dispatches, " + ((Time.realtimeSinceStartup - cpu0) * 1000f).ToString("0.00") + " ms CPU");
                Plugin.Log.LogInfo(TissueLine(_set.Softness, depthMul));
            }
        }

        // The anatomical dent caps at this softness, from the Tissue table: "Tissue: caps at softness 50 %: butt ~23 mm, ...".
        static string TissueLine(float softness, float depth)
        {
            Func<int, float, float, float> cap = (cls, share, fl) => Tissue.Cap(cls, share, fl, softness, depth) * 1000f;
            string f = "0";
            return "Tissue: caps at softness " + (softness * 100f).ToString("0") + " %" + (Mathf.Abs(depth - 1f) > 0.01f ? ", depth x" + depth.ToString("0.##") : "")
                + ": butt ~" + cap(ContactBake.ClsButt, 1f, -1f).ToString(f)
                + " mm, breast ~" + cap(ContactBake.ClsBreast, 1f, -1f).ToString(f)
                + ", belly ~" + cap(ContactBake.ClsBelly, 1f, -1f).ToString(f)
                + ", thigh " + cap(ContactBake.ClsThigh, 0f, -1f).ToString(f) + "-" + cap(ContactBake.ClsThigh, 1f, -1f).ToString(f)
                + ", calf " + cap(ContactBake.ClsCalf, 1f, -1f).ToString(f) + " (shin " + cap(ContactBake.ClsCalf, 0f, Tissue.ShinFloor).ToString(f) + ")"
                + ", upper arm " + cap(ContactBake.ClsUpperArm, 0f, Tissue.BicepsFloor).ToString(f) + "-" + cap(ContactBake.ClsUpperArm, 1f, -1f).ToString(f)
                + ", forearm " + cap(ContactBake.ClsForearm, 1f, -1f).ToString(f)
                + ", bony " + Mathf.Min(cap(ContactBake.ClsHead, 0f, -1f), Mathf.Min(cap(ContactBake.ClsHand, 0f, -1f), cap(ContactBake.ClsFoot, 0f, -1f))).ToString(f) + "-"
                + Mathf.Max(cap(ContactBake.ClsChest, 0f, -1f), Mathf.Max(cap(ContactBake.ClsSpine, 0f, -1f), cap(ContactBake.ClsPelvis, 0f, -1f))).ToString(f);
        }

        bool WaveReaches(Bounds b, int n)
        {
            for (int i = 0; i < n; i++)
            {
                Vector4 o = _waveArr[2 * i], p = _waveArr[2 * i + 1];
                float reach = Mathf.Min(p.y * o.w + 3f * p.z, 1f);   // the ring (c x age) and its width; beyond ~1 m the falloff (max(12 cm, 2 wavelengths)) has killed it
                if (b.SqrDistance(new Vector3(o.x, o.y, o.z)) <= reach * reach) return true;
            }
            return false;
        }

        void V2Fail(string where, Exception e)
        {
            if (_v2Failed) return;
            _v2Failed = true;
            Plugin.Log.LogError("GPU contact: v2 chain failed (" + where + "), back to the v1 imprint: " + e);
            UpdateStatus();
        }

        // One renderer's v2 chain (design 1.5): Depth -> Dilate x N -> Smooth x2 (narrow) -> Smooth x3 from it (wide) -> [MarkDisc] -> Compose -> Grad -> Apply.
        // Without contact (bend / wave / slap only) the field passes are skipped and Compose runs with _NoField; marks-only is one MarksDecay dispatch.
        // Returns the number of dispatches.
        int RunV2(RendState s, GraphicsBuffer vb, int nWaves, float dt, float softScale, float depthMul)
        {
            int groups = (s.Count + 63) / 64, n = 0;
            var cs = _cs;
            cs.SetInt(PCount, s.Count);
            cs.SetFloat(PDt, dt);
            cs.SetFloat(PMarkFade, Mathf.Max(0.5f, _set.MarkFade)); cs.SetFloat(PMarkGain, _set.MarkGain); cs.SetFloat(PDentFade, 1.5f);
            cs.SetInt(PMarkOn, s.MarkOn ? 1 : 0);
            Matrix4x4 toWorld = ToWorld(s);
            cs.SetInt(PStride, s.Stride); cs.SetInt(PPosOff, s.PosOff); cs.SetInt(PNrmOff, s.NrmOff); cs.SetInt(PTanOff, s.TanOff);
            cs.SetMatrix(PToWorld, toWorld); cs.SetMatrix(PToLocal, toWorld.inverse);
            cs.SetFloat(PNormalBend, _set.Normals ? 1f : 0f);
            if (s.MapOnly)   // nothing of its own: follow the body (and keep its marks fading / wipe them)
            {
                int m = 0;
                if (s.Decay) m += DecayMarks(s, groups, dt);
                return m + ApplyFinal(s, vb, groups, false);
            }
            float skirt = Mathf.Max(0.003f, _set.Skirt);
            cs.SetFloat(PSkirt, skirt); cs.SetFloat(PBulge, _set.Bulge); cs.SetFloat(PBulgeCap, 0.004f);
            cs.SetFloat(PSoftScale, softScale); cs.SetFloat(PDepth, depthMul); cs.SetFloat(PMargin, _margin);
            GraphicsBuffer A = s.FieldA, B = s.FieldB;
            if (s.Depth || s.Probe)
            {
                // Depth (also the probe): counts the pressed vertices; read back while her own limbs / his body are in reach, so the full chain
                // runs only while they really touch
                bool count = s.CapN > s.CapN1;
                if (count && !s.HitInflight) s.HitsBuf.SetData(ZeroHits);
                cs.SetInt(PCountHits, count ? 1 : 0);
                cs.SetBuffer(_kDepth, PVerts, vb); cs.SetBuffer(_kDepth, PCanon, s.CanonBuf); cs.SetBuffer(_kDepth, PTissue, s.TissueBuf);
                cs.SetBuffer(_kDepth, PClass, s.ClassBuf); cs.SetBuffer(_kDepth, PCaps, _capBuf); cs.SetBuffer(_kDepth, PFieldOut, s.FieldD);   // raw kept in D for Compose
                cs.SetBuffer(_kDepth, PHits, s.HitsBuf);
                if (_foreignMarks) cs.SetBuffer(_kDepth, PForeign, s.Foreign);
                cs.SetInt(PCapCount, s.CapN); cs.SetInt(PCapOff, s.CapOff);
                cs.Dispatch(_kDepth, groups, 1, 1); n++;
                if (count && !s.HitInflight) { s.HitInflight = true; AsyncGPUReadback.Request(s.HitsBuf, s.OnHits); }
            }
            if (s.Decay || (s.Probe && !s.Work))
            {
                if (s.Decay) n += DecayMarks(s, groups, dt);
                return n;
            }
            if (s.Depth)
            {
                // 0.11.7 (user: the dents look polygonal again): the passes are sized in metres, not in vertex hops. Fidelity's subdivision
                // halves the edges, so the fixed 2 + 3 smoothing passes (sized for her own mesh, ~7 mm edges) blurred half as far and the
                // dent kept the capsules' outline; the dilate was sized by the mean edge, which the dense fingers pull down to 2.4 mm (cap 10).
                float e = Mathf.Max(0.001f, s.Bake.SkinEdge > 0f ? s.Bake.SkinEdge : s.Bake.AvgEdge);
                int iters = Mathf.Clamp(Mathf.CeilToInt(3f * skirt / e), 2, 16);
                int narrow = Mathf.Clamp(Mathf.RoundToInt(0.012f / e), 2, 6);    // ~1.2 cm (2 passes at 7 mm)
                int wide = Mathf.Clamp(Mathf.RoundToInt(0.024f / e), 3, 10);     // ~2.4 cm (3 passes at 7 mm)
                // 0.8.9: Depth's raw field stays in D (Compose's floor); dilate D -> A, then A/B; narrow ends in C, wide in A; Compose writes B
                Pass(_kDilate, vb, s, s.FieldD, A, groups); n++;
                GraphicsBuffer cur = A, oth = B;
                for (int i = 1; i < iters; i++) { Pass(_kDilate, vb, s, cur, oth, groups); n++; var t = cur; cur = oth; oth = t; }
                for (int i = 0; i < narrow - 1; i++) { Pass(_kSmooth, vb, s, cur, oth, groups); n++; var t = cur; cur = oth; oth = t; }
                Pass(_kSmooth, vb, s, cur, s.FieldC, groups); n++;                                            // narrow -> C
                // wide from C: an odd count ends in A when it starts C -> A
                cur = s.FieldC; oth = A;
                for (int i = 0; i < wide; i++) { Pass(_kSmooth, vb, s, cur, oth, groups); n++; cur = oth; oth = ReferenceEquals(cur, A) ? B : A; }
                if (!ReferenceEquals(cur, A)) { Pass(_kSmooth, vb, s, cur, A, groups); n++; }                 // wide -> A
            }
            for (int d = 0; d < 4; d++)
            {
                if ((s.DiscMask & (1 << d)) == 0) continue;
                cs.SetBuffer(_kDisc, PVerts, vb); cs.SetBuffer(_kDisc, PCanon, s.CanonBuf); cs.SetBuffer(_kDisc, PMarks, s.MarksBuf);
                cs.SetVector(PDisc, _discArr[d]); cs.SetFloat(PDiscAmt, Mathf.Min(1f, _discAmt[d] * _set.MarkBoost));
                cs.Dispatch(_kDisc, groups, 1, 1); n++;
            }
            cs.SetBuffer(_kCompose, PVerts, vb); cs.SetBuffer(_kCompose, PCanon, s.CanonBuf); cs.SetBuffer(_kCompose, PTissue, s.TissueBuf);
            cs.SetBuffer(_kCompose, PBend, s.BendBuf); cs.SetBuffer(_kCompose, PFieldN, s.FieldC); cs.SetBuffer(_kCompose, PFieldW, A);
            cs.SetBuffer(_kCompose, PFieldR, s.FieldD); cs.SetBuffer(_kCompose, PFieldOut, B); cs.SetBuffer(_kCompose, PMarks, s.MarksBuf); cs.SetBuffer(_kCompose, PWaves, _waveBuf);
            if (_foreignMarks) cs.SetBuffer(_kCompose, PForeignR, s.Foreign);
            cs.SetInt(PNoField, s.Depth ? 0 : 1); cs.SetInt(PWaveCount, s.Wave ? nWaves : 0); cs.SetFloat(PWaveAmt, 1f); cs.SetFloat(PWaveImpact, Mathf.Clamp01(_set.RippleImpact));
            var amt = BendPose.Amt;
            cs.SetVector(PBendAmt0, new Vector4(amt[0], amt[1], amt[2], amt[3])); cs.SetVector(PBendAmt1, new Vector4(amt[4], amt[5], amt[6], amt[7]));
            cs.SetVector(PBendAmt2, new Vector4(amt[8], 0f, 0f, 0f));
            cs.Dispatch(_kCompose, groups, 1, 1); n++;
            cs.SetBuffer(_kGrad, PVerts, vb); cs.SetBuffer(_kGrad, PCanon, s.CanonBuf); cs.SetBuffer(_kGrad, PAdjOff, s.AdjOffBuf); cs.SetBuffer(_kGrad, PAdjV, s.AdjVBuf);
            cs.SetBuffer(_kGrad, PFieldIn, B); cs.SetBuffer(_kGrad, PGrad, s.GradBuf);
            cs.Dispatch(_kGrad, groups, 1, 1); n++;
            n += ApplyFinal(s, vb, groups, true);
            s.ChainRan = true;
            return n;
        }

        // Marks fading with nothing pressing, or wiped (marks switched off: a huge step), then MarkUntil cleared.
        int DecayMarks(RendState s, int groups, float dt)
        {
            var cs = _cs;
            cs.SetFloat(PDt, s.ClearMarks ? 1e4f : dt);
            cs.SetBuffer(_kDecay, PCanon, s.CanonBuf); cs.SetBuffer(_kDecay, PMarks, s.MarksBuf);
            cs.Dispatch(_kDecay, groups, 1, 1);
            cs.SetFloat(PDt, dt);
            if (s.ClearMarks) s.MarkUntil = 0f;
            return 1;
        }

        // The last step: Apply, or ApplyMap for an overlay with a body under it (its mapped vertices take the body's Grad of this frame when the
        // body ran its chain, the rest its own when `own`).
        int ApplyFinal(RendState s, GraphicsBuffer vb, int groups, bool own)
        {
            var cs = _cs;
            var par = s.Parent;
            if (par == null || s.MapBuf == null || s.MapWBuf == null || par.GradBuf == null)
            {
                if (!own) return 0;
                cs.SetBuffer(_kApply, PVerts, vb); cs.SetBuffer(_kApply, PCanon, s.CanonBuf); cs.SetBuffer(_kApply, PGrad, s.GradBuf);
                cs.Dispatch(_kApply, groups, 1, 1);
                return 1;
            }
            cs.SetBuffer(_kApplyMap, PVerts, vb); cs.SetBuffer(_kApplyMap, PCanon, s.CanonBuf); cs.SetBuffer(_kApplyMap, PGrad, s.GradBuf);
            cs.SetBuffer(_kApplyMap, PMap, s.MapBuf); cs.SetBuffer(_kApplyMap, PParentGrad, par.GradBuf); cs.SetBuffer(_kApplyMap, PMapW, s.MapWBuf);
            cs.SetInt(PParentOn, par.ChainRan ? 1 : 0); cs.SetInt(POwnOn, own ? 1 : 0);
            if (!par.ChainRan && !own) return 0;
            cs.Dispatch(_kApplyMap, groups, 1, 1);
            return 1;
        }

        // Dilate / Smooth: canonical vertices of `src` -> `dst`.
        void Pass(int kernel, GraphicsBuffer vb, RendState s, GraphicsBuffer src, GraphicsBuffer dst, int groups)
        {
            _cs.SetBuffer(kernel, PVerts, vb); _cs.SetBuffer(kernel, PCanon, s.CanonBuf); _cs.SetBuffer(kernel, PAdjOff, s.AdjOffBuf); _cs.SetBuffer(kernel, PAdjV, s.AdjVBuf);
            _cs.SetBuffer(kernel, PFieldIn, src); _cs.SetBuffer(kernel, PFieldOut, dst);
            _cs.Dispatch(kernel, groups, 1, 1);
        }

        // The skinned buffer's own stride is authoritative (the shader gets it, the calibration readback uses it); the mesh's stream-0 stride
        // is only logged. The buffer must hold position and normal Float32x3 at the mesh's offsets and one entry per vertex.
        bool CheckBuffer(RendState s, GraphicsBuffer vb)
        {
            string why = null;
            int meshStride = s.Stride;
            if (vb.count != s.Count) why = "buffer count " + vb.count + " != vertex count " + s.Count;
            else if (s.PosOff + 12 > vb.stride) why = "position (offset " + s.PosOff + ") does not fit buffer stride " + vb.stride;
            else if (s.NrmOff + 12 > vb.stride) why = "normal (offset " + s.NrmOff + ") does not fit buffer stride " + vb.stride;
            if (why != null)
            {
                s.Layout = -1;
                Plugin.Log.LogWarning("GPU contact: renderer " + s.Name + " skipped: " + why);
                UpdateStatus();
                return false;
            }
            s.Stride = vb.stride;
            if (s.TanOff >= 0 && s.TanOff + 16 > vb.stride)
            {
                Plugin.Log.LogWarning("GPU contact: renderer " + s.Name + ": tangent (offset " + s.TanOff + ") does not fit buffer stride " + vb.stride + ": tangents left alone");
                s.TanOff = -1;
            }
            s.Layout = 1;
            Plugin.Log.LogInfo("GPU contact: renderer " + s.Name + " skinned buffer ok: count " + vb.count + " stride " + vb.stride + " (mesh stream-0 stride " + meshStride
                + (meshStride != vb.stride ? ", differs: using the buffer's" : "") + ", target " + vb.target + ")");
            return true;
        }

        static Matrix4x4 ToWorld(RendState s)
        {
            switch (s.Space)
            {
                case 1: return (s.R.rootBone != null ? s.R.rootBone : s.R.transform).localToWorldMatrix;
                case 2: return s.R.transform.localToWorldMatrix;
                case 4: { var rb = s.R.rootBone != null ? s.R.rootBone : s.R.transform; return Matrix4x4.TRS(rb.position, rb.rotation, Vector3.one); }
                default: return Matrix4x4.identity;
            }
        }

        // ---- space calibration ----

        // Reads the first vertices of the skinned buffer back and compares them with a CPU skin of the same frame under each candidate space.
        void RequestCalibration(RendState s, GraphicsBuffer vb)
        {
            s.CalibTries++;
            s.CalibNextFrame = Time.frameCount + CalibRetryFrames;
            var r = s.R;
            var m = r.sharedMesh;
            var bones = r.bones;
            var bind = m.bindposes;
            if (!m.isReadable)
            {
                // 0.11.7: her face and neck are GPU-only meshes (no CPU vertices to skin), so they never calibrated and had no dents or
                // marks since 0.9.7. Unity skins every renderer of one rig into the same convention: take the space a readable renderer
                // of hers found (rootBone in every log so far), waiting a few tries for one to finish.
                RendState from = null;
                foreach (var o in _rs)
                    if (o != s && o.Space > 0 && o.R != null && (from == null || (o.Space == 1 && from.Space != 1))) from = o;
                if (from != null)
                {
                    s.Space = from.Space;
                    Plugin.Log.LogInfo("GPU contact: renderer " + s.Name + " space " + SpaceNames[s.Space] + " taken from " + from.Name + " (its mesh is not readable for a CPU check)");
                    UpdateStatus();
                }
                else if (s.CalibTries >= CalibMaxTries + 3) CalibFail(s, "mesh is not readable and no other renderer of hers calibrated");
                return;
            }
            var bw = m.boneWeights;
            var verts = m.vertices;
            if (bw.Length != verts.Length || bones.Length != bind.Length || bones.Length == 0) { CalibFail(s, "no usable bone weights/bindposes (bones " + bones.Length + ", bindposes " + bind.Length + ")"); return; }
            int n = Mathf.Min(CalibVerts, s.Count);
            // the skinning input: base vertices plus the renderer's active blendshapes (last frame of each shape x weight/100)
            var bv = new Vector3[n];
            for (int i = 0; i < n; i++) bv[i] = verts[i];
            int vc = m.vertexCount;
            for (int b = 0; b < m.blendShapeCount; b++)
            {
                float bwt = r.GetBlendShapeWeight(b) / 100f;
                if (bwt <= 0f) continue;
                int fc = m.GetBlendShapeFrameCount(b);
                if (fc <= 0) continue;
                if (_dv == null || _dv.Length != vc) _dv = new Vector3[vc];
                m.GetBlendShapeFrameVertices(b, fc - 1, _dv, null, null);
                for (int i = 0; i < n; i++) bv[i] += _dv[i] * bwt;
            }
            var bm = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++) bm[i] = bones[i] != null ? bones[i].localToWorldMatrix * bind[i] : Matrix4x4.zero;
            var pw = new Vector3[n];
            var valid = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var w = bw[i];
                Vector3 v = bv[i], acc = Vector3.zero;
                float sum = 0f;
                if (w.weight0 > 0f) { acc += bm[w.boneIndex0].MultiplyPoint3x4(v) * w.weight0; sum += w.weight0; }
                if (w.weight1 > 0f) { acc += bm[w.boneIndex1].MultiplyPoint3x4(v) * w.weight1; sum += w.weight1; }
                if (w.weight2 > 0f) { acc += bm[w.boneIndex2].MultiplyPoint3x4(v) * w.weight2; sum += w.weight2; }
                if (w.weight3 > 0f) { acc += bm[w.boneIndex3].MultiplyPoint3x4(v) * w.weight3; sum += w.weight3; }
                pw[i] = acc;
                valid[i] = sum > 0.5f;
            }
            // candidate buffer-to-world matrices, duplicates (rootBone == null or == transform, unscaled == scaled) dropped
            var cm = new List<Matrix4x4>(4);
            var ci = new List<int>(4);
            for (int k = 1; k <= 4; k++)
            {
                if (k == 4 && r.rootBone == null) continue;
                Matrix4x4 c = k == 1 ? (r.rootBone != null ? r.rootBone : r.transform).localToWorldMatrix : k == 2 ? r.transform.localToWorldMatrix
                    : k == 3 ? Matrix4x4.identity : Matrix4x4.TRS(r.rootBone.position, r.rootBone.rotation, Vector3.one);
                bool dup = false;
                for (int j = 0; j < cm.Count && !dup; j++) dup = Same(cm[j], c);
                if (!dup) { cm.Add(c); ci.Add(k); }
            }
            int stride = s.Stride, posOff = s.PosOff;
            int gen = s.CalibTries;
            s.CalibState = 1;
            AsyncGPUReadback.Request(vb, n * stride, 0, req =>
            {
                try
                {
                    if (!s.Alive || _dead) return;
                    s.CalibState = 0;
                    if (req.hasError) { Plugin.Log.LogWarning("GPU contact: calibration readback failed for " + s.Name + " (try " + gen + ")"); return; }
                    var data = req.GetData<float>();   // laid out with the buffer's own stride (s.Stride was set from it)
                    var dist = new List<float>[cm.Count];
                    for (int k = 0; k < cm.Count; k++) dist[k] = new List<float>(n);
                    int cnt = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (!valid[i]) continue;
                        int f = (i * stride + posOff) / 4;
                        if (f + 2 >= data.Length) break;
                        var p = new Vector3(data[f], data[f + 1], data[f + 2]);
                        for (int k = 0; k < cm.Count; k++) dist[k].Add((cm[k].MultiplyPoint3x4(p) - pw[i]).magnitude);
                        cnt++;
                    }
                    if (cnt < 16) { Plugin.Log.LogWarning("GPU contact: calibration of " + s.Name + " had too few vertices (" + cnt + ")"); return; }
                    var mean = new double[cm.Count];
                    var med = new double[cm.Count];
                    for (int k = 0; k < cm.Count; k++)
                    {
                        double sum = 0.0;
                        for (int i = 0; i < dist[k].Count; i++) sum += dist[k][i];
                        mean[k] = sum / cnt;
                        dist[k].Sort();
                        int h = dist[k].Count / 2;
                        med[k] = (dist[k].Count & 1) == 1 ? dist[k][h] : 0.5 * (dist[k][h - 1] + dist[k][h]);
                    }
                    int best = 0;
                    for (int k = 1; k < cm.Count; k++) if (med[k] < med[best]) best = k;
                    double bestE = med[best], second = double.MaxValue;
                    for (int k = 0; k < cm.Count; k++) if (k != best) second = Math.Min(second, med[k]);
                    string errs = "";
                    for (int k = 0; k < cm.Count; k++)
                        errs += (k > 0 ? ", " : "") + SpaceNames[ci[k]] + " median " + (med[k] * 1000.0).ToString("0.0") + " / mean " + (mean[k] * 1000.0).ToString("0.0") + " mm";
                    if (bestE < CalibAccept && (second > CalibSeparate || second > CalibRelative * bestE + CalibRelFloor))
                    {
                        s.Space = ci[best];
                        Plugin.Log.LogInfo("GPU contact: renderer " + s.Name + " space " + SpaceNames[s.Space] + " (" + errs + ", " + cnt + " vertices)");
                    }
                    else
                    {
                        Plugin.Log.LogWarning("GPU contact: renderer " + s.Name + " space ambiguous (" + errs + ", " + cnt + " vertices), try " + gen + "/" + CalibMaxTries
                            + (gen >= CalibMaxTries ? ": imprint stays off for it" : ""));
                        if (gen >= CalibMaxTries) s.Space = -1;
                    }
                    UpdateStatus();
                }
                catch (Exception e) { Plugin.Log.LogError("GPU contact: calibration callback failed, turned off: " + e); GoDead("error"); }
            });
        }

        void CalibFail(RendState s, string why)
        {
            s.Space = -1;
            Plugin.Log.LogWarning("GPU contact: renderer " + s.Name + " cannot be calibrated (" + why + "): imprint stays off for it");
            UpdateStatus();
        }

        static bool Same(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++) if (Mathf.Abs(a[i] - b[i]) > 1e-5f) return false;
            return true;
        }

        // ---- status / shutdown ----

        void UpdateStatus()
        {
            if (_dead) return;
            int n = 0, space = 0, v2 = 0;
            bool mixed = false, useV2 = _v2Kernels && !_v2Failed;
            for (int i = 0; i < _rs.Count; i++)
            {
                var s = _rs[i];
                if (!s.Ok) continue;
                n++;
                if (useV2 && s.V2) v2++;
                if (s.Space > 0) { if (space == 0) space = s.Space; else if (space != s.Space) mixed = true; }
            }
            var sb = new StringBuilder("GPU contact: on");
            if (v2 > 0) sb.Append(" (v2)");
            sb.Append(", ").Append(n).Append(" renderers");
            if (v2 > 0 && v2 < n) sb.Append(" (").Append(n - v2).Append(" on the v1 imprint)");
            sb.Append(", space ").Append(mixed ? "mixed" : SpaceNames[space]);
            if (v2 > 0)
            {
                sb.Append(", skirt ").Append((_set.Skirt * 1000f).ToString("0")).Append(" mm, bulge ").Append(_set.Bulge.ToString("0.0#")).Append(_set.Normals ? ", normals" : ", positions only");
                sb.Append(_set.SelfContact ? ", self " + _selfN + " capsules" : ", self contact off").Append(_set.PartnerBody ? ", partner body " + _pbN : ", partner body off");
                sb.Append(", bend ").Append(BendPose.Joints).Append(" joints");
            }
            else if (_v2Failed) sb.Append(", v2 failed (see the log)");
            Status = sb.ToString();
        }

        void GoDead(string why)
        {
            _dead = true;
            Status = "GPU contact: off: " + why;
            LastStatus = Status;
            Unsubscribe();
        }

        void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            RenderPipelineManager.beginContextRendering -= OnBeginContext;
        }

        // ---- PlayerLoop fallback (same pattern as ApplyStep.Ensure): a step right after PostLateUpdate.UpdateAllSkinnedMeshes ----

        static bool InsertStep(PlayerLoopSystem.UpdateFunction fn)
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var subs = root.subSystemList;
            if (subs == null) return false;
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type != typeof(PostLateUpdate) || subs[i].subSystemList == null) continue;
                var list = new List<PlayerLoopSystem>(subs[i].subSystemList);
                list.RemoveAll(x => x.type == typeof(GpuContactStep));   // a stale step of an earlier instance
                int at = list.FindIndex(x => x.type == typeof(PostLateUpdate.UpdateAllSkinnedMeshes));
                if (at < 0) return false;
                list.Insert(at + 1, new PlayerLoopSystem { type = typeof(GpuContactStep), updateDelegate = fn });
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
                if (list.RemoveAll(x => x.type == typeof(GpuContactStep)) == 0) return;
                subs[i].subSystemList = list.ToArray();
                root.subSystemList = subs;
                PlayerLoop.SetPlayerLoop(root);
                return;
            }
        }

        // Frees everything that touches Unity objects (restores vertexBufferTarget, buffers, bundle). Safe to call twice.
        void Teardown()
        {
            Unsubscribe();
            if (_inLoop) { _inLoop = false; try { RemoveStep(); } catch (Exception e) { Plugin.Log.LogWarning("GPU contact: removing the loop step failed: " + e.Message); } }
            for (int i = 0; i < _rs.Count; i++) { try { DropState(_rs[i]); } catch (Exception e) { Plugin.Log.LogWarning("GPU contact: restore failed: " + e.Message); } }
            _rs.Clear();
            Dispose();
        }

        void Dispose()
        {
            if (_capBuf != null) { _capBuf.Release(); _capBuf = null; }
            if (_waveBuf != null) { _waveBuf.Release(); _waveBuf = null; }
            _cs = null;
            if (_bundleHeld) { _bundleHeld = false; ShaderBundle.Release(); }
        }

        public void Release()
        {
            try { Teardown(); }
            catch (Exception e) { Plugin.Log.LogWarning("GPU contact: release failed: " + e.Message); }
            _dead = true;
            _z = null;
            Status = "";
            Plugin.Log.LogInfo("GPU contact: released");
        }
    }
}
