using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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

        [StructLayout(LayoutKind.Sequential)]
        struct CapData { public Vector4 A, B; }   // a.xyz + radius, b.xyz  (stride 32, mirrors the compute shader's Capsule)

        const int MaxCaps = 256;
        const int CalibVerts = 256;
        const float CalibAccept = 0.02f, CalibSeparate = 0.10f;
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
        }

        static readonly int PVerts = Shader.PropertyToID("_Verts"), PStride = Shader.PropertyToID("_Stride"),
            PPosOff = Shader.PropertyToID("_PosOff"), PNrmOff = Shader.PropertyToID("_NrmOff"), PCount = Shader.PropertyToID("_Count"),
            PToWorld = Shader.PropertyToID("_ToWorld"), PToLocal = Shader.PropertyToID("_ToLocal"),
            PCaps = Shader.PropertyToID("_Caps"), PCapCount = Shader.PropertyToID("_CapCount"), PMask = Shader.PropertyToID("_Mask"),
            PLimit = Shader.PropertyToID("_Limit"), PMargin = Shader.PropertyToID("_Margin"), PSmoke = Shader.PropertyToID("_SmokeOffset");

        static readonly string[] SpaceNames = { "pending", "rootBone", "transform", "identity", "rootBone (unscaled)" };

        bool _bundleHeld;                     // we hold a ShaderBundle reference
        ComputeShader _cs;
        int _kSmoke, _kImprint;
        ComputeBuffer _capBuf;
        readonly CapData[] _capArr = new CapData[MaxCaps];
        List<SoftCapsule> _capList;           // the caller's list; converted to world space inside the dispatch
        int _capCount, _capFrame = -1000;
        Vector3[] _dv;                        // blendshape delta scratch for the calibration's CPU skin
        bool _inLoop;
        float _limit = 0.02f, _margin = 0.003f;
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
                g._kSmoke = g._cs.FindKernel("Smoke");
                g._kImprint = g._cs.FindKernel("Imprint");
                Plugin.Log.LogInfo("GPU contact: bundle loaded, kernels Smoke=" + g._kSmoke + " Imprint=" + g._kImprint);
                if (g._kSmoke < 0 || g._kImprint < 0) { Fail("kernel"); g.Dispose(); return null; }
                g._capBuf = new ComputeBuffer(MaxCaps, 32, ComputeBufferType.Structured);
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

        static void Fail(string why)
        {
            LastStatus = "GPU contact: off: " + why;
            Plugin.Log.LogWarning(LastStatus);
        }

        // ---- sync with the soft zones' build ----

        public void Sync(SoftZones z)
        {
            try
            {
                if (_dead) { Teardown(); return; }
                if (z == null) { _z = null; _syncVersion = int.MinValue; RemoveRenderers(null); UpdateStatus(); return; }
                if (z == _z && z.BuildVersion == _syncVersion) return;
                _z = z;
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
                        SetupMesh(s, mask);
                    }
                RemoveRenderers(keep);
                UpdateStatus();
            }
            catch (Exception e) { Plugin.Log.LogError("GPU contact: sync failed, turned off: " + e); GoDead("error"); }
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

        // Mesh-dependent state: layout from the mesh, the mask buffer. The skinned-buffer check (stride/count) follows on the first frame.
        void SetupMesh(RendState s, float[] mask)
        {
            var m = s.R.sharedMesh;
            s.Mesh = m;
            s.Layout = 0;
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
            Plugin.Log.LogInfo("GPU contact: renderer " + s.Name + " mesh " + m.name + " count " + s.Count + " stride " + s.Stride + " posOff " + s.PosOff + " nrmOff " + s.NrmOff
                + " (Raw set by us: " + RawTarget.SetByUs(s.R) + ")" + (s.Space > 0 ? ", space " + SpaceNames[s.Space] + " (kept)" : ""));
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

        static void DropState(RendState s)
        {
            s.Alive = false;
            RestoreRaw(s);
            if (s.Mask != null) { s.Mask.Release(); s.Mask = null; }
        }

        // ---- per frame inputs ----

        // Stores the caller's list; the world-space conversion and the upload happen inside the dispatch (the hook / loop step).
        public void SetCapsules(List<SoftCapsule> caps, float limit, float margin)
        {
            try
            {
                if (_dead) { Teardown(); return; }
                _limit = limit; _margin = margin;
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

        // World-space capsules for this frame's dispatch (T == null, the excluded kinds and the capsules of hidden sources (0.7.7) skipped, at most MaxCaps).
        int UploadCapsules()
        {
            int n = 0;
            var caps = _capList;
            if (caps != null)
                for (int i = 0; i < caps.Count && n < MaxCaps; i++)
                {
                    var c = caps[i];
                    if (c.T == null || c.Kind == "thighL" || c.Kind == "thighR" || c.Kind == "forearm" || c.Kind == "hand") continue;
                    if (c.Src != null && !c.Src.Live) continue;   // a hidden hand / partner doesn't imprint her
                    Vector3 a = c.T.TransformPoint(c.LocalA), b = c.T.TransformPoint(c.LocalB);
                    _capArr[n].A = new Vector4(a.x, a.y, a.z, c.Radius);
                    _capArr[n].B = new Vector4(b.x, b.y, b.z, 0f);
                    n++;
                }
            _capCount = n;
            if (n > 0) _capBuf.SetData(_capArr, 0, 0, n);
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

        // The dispatch itself; called by the loop step and by the SRP hook, whichever comes first in a frame wins (shared frame guard).
        void Run()
        {
            {
                int frame = Time.frameCount;
                if (frame == _lastFrame) return;   // Imprint is not idempotent
                _lastFrame = frame;
                _evSeen = true;
                // before anything of ours writes the skinned buffers: the wrinkle strain must read the clean skinning result
                var before = BeforeImprint;
                if (before != null)
                    try { before(frame); }
                    catch (Exception e) { Plugin.Log.LogWarning("GPU contact: BeforeImprint handler failed: " + e.Message); }
                if (_rs.Count == 0) return;
                float now = Time.realtimeSinceStartup;
                float smokeT = _smokeDur > 0f ? (now - _smokeStart) / _smokeDur : 2f;
                float smoke = smokeT >= 0f && smokeT < 1f ? _smokeMetres * Mathf.Sin(Mathf.PI * smokeT) : 0f;
                bool imprint = _capList != null && frame - _capFrame <= 3 && UploadCapsules() > 0;
                for (int i = 0; i < _rs.Count; i++)
                {
                    var s = _rs[i];
                    if (s.R == null || !s.R.enabled || !s.R.gameObject.activeInHierarchy) continue;
                    if (s.Mesh != s.R.sharedMesh && _z != null) { SetupMesh(s, _z.MaskFor(s.R)); }   // the mesh was swapped under us
                    if (!s.Ok) continue;
                    bool needCalib = s.Space == 0 && s.CalibState == 0 && s.CalibTries < CalibMaxTries && frame >= s.CalibNextFrame;
                    bool doImprint = imprint && s.Space > 0;
                    if (!needCalib && !doImprint && smoke == 0f) continue;
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
                        if (needCalib) { RequestCalibration(s, vb); continue; }   // read the untouched skinning result first
                        int groups = (s.Count + 63) / 64;
                        if (doImprint)
                        {
                            Matrix4x4 toWorld = ToWorld(s);
                            _cs.SetBuffer(_kImprint, PVerts, vb);
                            _cs.SetBuffer(_kImprint, PMask, s.Mask);
                            _cs.SetBuffer(_kImprint, PCaps, _capBuf);
                            _cs.SetInt(PStride, s.Stride); _cs.SetInt(PPosOff, s.PosOff); _cs.SetInt(PNrmOff, s.NrmOff); _cs.SetInt(PCount, s.Count);
                            _cs.SetInt(PCapCount, _capCount);
                            _cs.SetMatrix(PToWorld, toWorld); _cs.SetMatrix(PToLocal, toWorld.inverse);
                            _cs.SetFloat(PLimit, _limit); _cs.SetFloat(PMargin, _margin);
                            _cs.Dispatch(_kImprint, groups, 1, 1);
                        }
                        if (smoke != 0f)
                        {
                            _cs.SetBuffer(_kSmoke, PVerts, vb);
                            _cs.SetInt(PStride, s.Stride); _cs.SetInt(PPosOff, s.PosOff); _cs.SetInt(PNrmOff, s.NrmOff); _cs.SetInt(PCount, s.Count);
                            _cs.SetFloat(PSmoke, smoke);
                            _cs.Dispatch(_kSmoke, groups, 1, 1);
                        }
                        if (!_loggedFirstRun) { _loggedFirstRun = true; Plugin.Log.LogInfo("GPU contact: first dispatch on " + s.Name + " (" + (doImprint ? "imprint " : "") + (smoke != 0f ? "smoke" : "") + ")"); }
                    }
                }
            }
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
            if (!m.isReadable) { CalibFail(s, "mesh is not readable"); return; }
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
                    if (bestE < CalibAccept && second > CalibSeparate)
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
            int n = 0, space = 0;
            bool mixed = false;
            for (int i = 0; i < _rs.Count; i++)
            {
                var s = _rs[i];
                if (!s.Ok) continue;
                n++;
                if (s.Space > 0) { if (space == 0) space = s.Space; else if (space != s.Space) mixed = true; }
            }
            Status = "GPU contact: on, " + n + " renderers, space " + (mixed ? "mixed" : SpaceNames[space]);
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
