using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // 0.11.47 smoke tests (docs/LESSONS.md: every feature ships with a Debug-tab smoke test). Nothing here can run outside the game, so each
    // feature has a test the user presses in the headset (Debug tab -> Smoke tests, or the feature's own section); each test exercises the
    // feature without changing what she looks like for good (whatever it touches it gives back: a forced wrinkle region, an injected face
    // curve, the pose driver's weights) and logs exactly one line:
    //     Smoke: <id> PASS|FAIL|SKIP (<why, with the key numbers>)
    // A FAIL says what to look at next. The log then tells the next session what works.
    // Tests that need time (a dent for 1 s, a region forced for a moment, soft-zone steps over a second) run as a small state machine
    // stepped from Runner.Update (before Body.ApplyAll in the same frame), never blocking. Idle (no test running) Tick returns at once:
    // no allocation, no work. "Run all" runs them in turn, one per frame at the earliest.
    internal static class Smoke
    {
        public static readonly string[] Ids =
        {
            "shaders", "gpu-contact", "cull", "delta-mush", "wrinkles", "curve-wrinkles", "muscles", "correctives", "quality", "lip-sync",
            "soft-zones", "physics-hands", "skin-marks", "frame-timing",
        };
        public static readonly string[] Labels =
        {
            "Shader bundle and kernels", "GPU contact (1 s dent)", "Contact cull", "Delta Mush", "Wrinkles (regions, a re-blend)",
            "Curve-driven wrinkles (1 s)", "Muscles (lengths)", "Pose correctives", "Quality presets", "Lip sync", "Soft zones (1 s)",
            "Physics hands", "Skin marks (alive)", "Frame timing (clock self-test)",
        };
        public static readonly string[] Buttons = Prefixed("Smoke: ");
        static string[] Prefixed(string pre) { var r = new string[Labels.Length]; for (int i = 0; i < r.Length; i++) r[i] = pre + Labels[i]; return r; }
        public const int TShaders = 0, TGpu = 1, TCull = 2, TMush = 3, TWrinkles = 4, TCurve = 5, TMuscles = 6, TCorrectives = 7, TQuality = 8,
                         TLip = 9, TSoft = 10, THands = 11, TMarks = 12, TTiming = 13;
        public static readonly int Count = Ids.Length;
        // the last result of each test this session ("" = not run): "PASS (...)" / "FAIL (...)" / "SKIP (...)"
        public static readonly string[] Results = NewResults();
        const float Timeout = 8f;

        static int _cur = -1, _phase, _next = -1;
        static float _t0;
        static bool _all;
        static int _pass, _fail, _skip;

        static string[] NewResults() { var r = new string[Ids.Length]; for (int i = 0; i < r.Length; i++) r[i] = ""; return r; }

        public static bool Running { get { return _cur >= 0; } }
        public static bool RunningAll { get { return _all; } }
        public static int Current { get { return _cur; } }
        public static float Elapsed { get { return _cur >= 0 ? Time.unscaledTime - _t0 : 0f; } }

        public static void Run(int i)
        {
            if (_cur >= 0 || i < 0 || i >= Count) return;
            _all = false;
            Begin(i);
        }

        public static void RunAll()
        {
            if (_cur >= 0) return;
            _all = true; _pass = _fail = _skip = 0;
            Plugin.Log.LogInfo("Smoke run: " + Count + " tests (SF Body Morphs " + Plugin.Version + ")");
            Begin(0);
        }

        public static void Stop()
        {
            if (_cur < 0) return;
            Finish("SKIP (stopped from the panel)");
            _all = false; _next = -1;
        }

        static void Begin(int i)
        {
            _cur = i; _phase = 0; _t0 = Time.unscaledTime; _next = -1;
            Results[i] = "running";
        }

        static void Finish(string result)
        {
            int i = _cur;
            if (i < 0) return;
            try { Cleanup(i); } catch (Exception e) { Plugin.Log.LogWarning("Smoke: " + Ids[i] + " cleanup failed: " + e.Message); }
            Results[i] = result;
            Plugin.Log.LogInfo("Smoke: " + Ids[i] + " " + result);
            if (result.StartsWith("PASS", StringComparison.Ordinal)) _pass++;
            else if (result.StartsWith("FAIL", StringComparison.Ordinal)) _fail++;
            else _skip++;
            _cur = -1;
            if (_all)
            {
                if (i + 1 < Count) _next = i + 1;
                else
                {
                    _all = false;
                    Plugin.Log.LogInfo("Smoke run done: " + _pass + " PASS, " + _fail + " FAIL, " + _skip + " SKIP of " + Count);
                }
            }
        }

        // Runner.Update, every frame. Idle: returns at once.
        public static void Tick(Plugin p, Body b)
        {
            if (_cur < 0)
            {
                if (_next < 0) return;
                int n = _next;
                Begin(n);   // the next test of "Run all" starts a frame after the last one ended
            }
            float t = Time.unscaledTime - _t0;
            string r;
            try { r = Step(_cur, p, b, t); }
            catch (Exception e) { r = "FAIL (the test threw " + e.GetType().Name + ": " + e.Message + "; a bug in Smoke.cs or the feature, see the log)"; Plugin.Log.LogWarning("Smoke: " + Ids[_cur] + ": " + e); }
            if (r == null && t > Timeout) r = "FAIL (timed out after " + Timeout.ToString("0", CultureInfo.InvariantCulture) + " s in step " + _phase + ")";
            if (r != null) Finish(r);
        }

        static string Step(int i, Plugin p, Body b, float t)
        {
            switch (i)
            {
                case TShaders: return Shaders(p, b);
                case TGpu: return Gpu(p, b, t);
                case TCull: return Cull(p, b, t);
                case TMush: return Mush(p, b);
                case TWrinkles: return WrinklesT(p, b, t);
                case TCurve: return Curve(p, b, t);
                case TMuscles: return MusclesT(p, b);
                case TCorrectives: return Correctives(p, b);
                case TQuality: return QualityT(p);
                case TLip: return Lip(p, b);
                case TSoft: return Soft(p, b, t);
                case THands: return Hands(p);
                case TTiming: return Timing(p);
                default: return Marks(p, b);
            }
        }

        // whatever a test changed goes back (also on Stop / an exception / a timeout)
        static void Cleanup(int i)
        {
            if (i == TWrinkles) WrinkleTest.Unforce();
            if (i == TCurve) FaceCurves.ClearInjected();
        }

        // ---- helpers ----

        static string F(float v, string fmt) { return v.ToString(fmt, CultureInfo.InvariantCulture); }
        static string Pass(string why) { return "PASS (" + why + ")"; }
        static string Fail(string why) { return "FAIL (" + why + ")"; }
        static string Skip(string why) { return "SKIP (" + why + ")"; }

        static Character Her(Body b)
        {
            foreach (var c in b.Found) if (c.Female && !c.IsPlayer && c.Root != null) return c;
            return null;
        }

        // why a test that needs her can't run ("" = it can)
        static string NoHer(Plugin p, Body b, out Character her)
        {
            her = Her(b);
            if (her == null) return "no woman in the scene";
            if (!p.Apply.Value) return "'Apply changes' is off";
            return "";
        }

        static string GpuOff(Plugin p, Body b)
        {
            if (!p.GpuContactOn.Value) return "GPU contact is off (Physics tab)";
            if (!p.Physics.Value) return "soft zones are off (GPU contact follows them)";
            return b.Gpu == null ? null : "";
        }

        // ---- the tests ----

        // The shader bundle as GPU contact and the wrinkles loaded it: every kernel / marker kernel this DLL dispatches or checks for, the
        // wrinkle raster, its layouts (with the 0.11.47 body regions) and the muscle layers.
        static string Shaders(Plugin p, Body b)
        {
            var gpu = b.Gpu; var wr = b.Wrink;
            var sb = new StringBuilder();
            var bad = new StringBuilder();
            if (gpu != null)
            {
                if (gpu.Dead) bad.Append("GPU contact turned itself off (" + gpu.Status + "); ");
                string miss = gpu.MissingKernels();
                if (miss.Length > 0) bad.Append("SoftDeform lacks " + miss + " (install the dist/sfmorph_shaders that came with this DLL); ");
                else sb.Append("SoftDeform: all " + GpuContact.SmokeKernelCount + " kernels incl. MushWeighted, MuscleFlex, Folds");
                if (!gpu.V2Ok) bad.Append("v2 chain failed (look for 'v2 chain failed' in the log); ");
            }
            else if (p.GpuContactOn.Value && p.Physics.Value && p.Apply.Value && Her(b) != null)
                bad.Append("GPU contact is on but not loaded: " + b.GpuStatus + "; ");
            if (wr != null)
            {
                if (wr.Dead) bad.Append("wrinkles turned themselves off (" + wr.Status + "); ");
                string missR = "";
                foreach (var r in NewBody) if (!wr.HasRegion(r)) missR += (missR.Length > 0 ? " " : "") + r;
                if (missR.Length > 0) bad.Append("wrinkle layouts lack " + missR + " (old sfmorph_shaders); ");
                if (!wr.HasMuscles) bad.Append("no muscle layer (the bundle's WrinkleRaster or muscle_layouts is old); ");
                if (!wr.HasCavity) bad.Append("WrinkleRaster has no crease-shade pass; ");
                sb.Append(sb.Length > 0 ? "; " : "").Append("WrinkleRaster 3 passes, layouts ").Append(wr.LayoutSummary()).Append(" (5 new body regions there), ")
                  .Append(wr.MuscleLayerCount).Append(" muscle layer(s)");
            }
            else if ((p.WrinkOn.Value || p.MuscleOn.Value) && p.Apply.Value && Her(b) != null)
                bad.Append("wrinkles / muscles are on but not loaded: " + b.WrinkleStatus + "; ");
            if (bad.Length > 0) return Fail(bad.ToString().TrimEnd(' ', ';'));
            if (gpu == null && wr == null) return Skip("neither GPU contact nor wrinkles / muscles hold the bundle (switch one on with her in the scene)");
            return Pass(sb.ToString());
        }

        // The existing GPU check (her skin sunk 1 cm for 1 s): its dispatches must run in the render hook.
        static int _d0;
        static string Gpu(Plugin p, Body b, float t)
        {
            Character her;
            string no = NoHer(p, b, out her);
            if (no.Length > 0) return Skip(no);
            string off = GpuOff(p, b);
            if (off == null) return Fail("on but not loaded: " + b.GpuStatus);
            if (off.Length > 0) return Skip(off);
            var gpu = b.Gpu;
            if (_phase == 0)
            {
                if (gpu.Dead) return Fail("GPU contact turned itself off: " + gpu.Status);
                _d0 = gpu.SmokeDispatches;
                gpu.Smoke(1f, 0.01f);
                _phase = 1;
                return null;
            }
            if (t < 1.2f) return null;
            int d = gpu.SmokeDispatches - _d0, n = gpu.RendererCount;
            if (d <= 0) return Fail("no dent dispatch in 1.2 s on " + n + " renderers: the render hook did not run (log: 'GPU contact: PlayerLoop step', errors after 'GPU contact:')");
            return Pass(d + " dent dispatches over 1 s on " + n + " renderers, " + gpu.Status);
        }

        // The view / distance cull: the setting reaches GPU contact, and no renderer is culled while in view and near.
        static int _cf, _cWrong, _cMax;
        static string Cull(Plugin p, Body b, float t)
        {
            Character her;
            string no = NoHer(p, b, out her);
            if (no.Length > 0) return Skip(no);
            string off = GpuOff(p, b);
            if (off == null) return Fail("on but not loaded: " + b.GpuStatus);
            if (off.Length > 0) return Skip(off);
            var gpu = b.Gpu;
            int total, vis, far, culled, wrong;
            gpu.CullStats(out total, out vis, out far, out culled, out wrong);
            if (_phase == 0) { _phase = 1; _cf = 0; _cWrong = 0; _cMax = 0; }
            _cf++;
            if (wrong > 0) _cWrong++;
            if (culled > _cMax) _cMax = culled;
            if (t < 0.5f) return null;
            float set = p.ContactCullDistance.Value;
            if (Mathf.Abs(gpu.CullSetting - set) > 0.01f)
                return Fail("[Physics] ContactCullDistance " + F(set, "0.0") + " m but GPU contact uses " + F(gpu.CullSetting, "0.0") + " m (Body.FillContact)");
            string dist = "?";
            var cam = GameRig.VrCamera();
            if (cam != null)
            {
                float best = float.MaxValue;
                foreach (var r in her.Renderers) if (r != null && r.enabled) best = Mathf.Min(best, r.bounds.SqrDistance(cam.transform.position));
                if (best < float.MaxValue) dist = F(Mathf.Sqrt(best), "0.0");
            }
            string nums = total + " renderers: " + vis + " in view, " + far + " beyond " + (set > 0f ? F(set, "0.0") + " m" : "(distance test off)") + ", up to " + _cMax +
                          " culled over " + _cf + " frames; headset " + dist + " m from her";
            if (total == 0) return Fail("GPU contact has no renderer of hers (status: " + gpu.Status + ")");
            if (_cWrong * 2 > _cf) return Fail(nums + "; renderers culled while in view and near in " + _cWrong + " frames (GpuContact cull plan)");
            return Pass(nums);
        }

        // Delta Mush as baked: kernels, joint vertices per joint, rest weights uploaded, per-joint strengths wired, ran last frame. Whether a
        // joint strength of 0 vs 1 changes the output would need a GPU readback of the skinned buffer: not measured, the bound state is.
        static readonly int[] _perJoint = new int[4];
        static string Mush(Plugin p, Body b)
        {
            Character her;
            string no = NoHer(p, b, out her);
            if (no.Length > 0) return Skip(no);
            string off = GpuOff(p, b);
            if (off == null) return Fail("GPU contact on but not loaded: " + b.GpuStatus);
            if (off.Length > 0) return Skip(off);
            var gpu = b.Gpu;
            if (!gpu.MushKernelsOk) return Fail("Delta Mush off in GPU contact: " + gpu.MushNote + " (install the bundle that came with this DLL; 'Delta Mush failed' in the log)");
            int baked, weighted, core, passes, badW;
            gpu.MushStats(_perJoint, out baked, out weighted, out core, out passes, out badW);
            if (baked == 0)
                return p.DeltaMush.Value ? Fail("on but nothing baked on her body meshes (log: 'Delta Mush' bake lines, " + gpu.Status + ")")
                                         : Skip("Round joints (Delta Mush) is off and nothing is baked");
            var jc = GpuContact.MushJointCfg;
            if (jc == null || jc.Length < 4 || GpuContact.MushKeepCfg == null) return Fail("the per-joint strengths / keep volume are not wired to the config (Plugin.Awake)");
            Vector4 js = GpuContact.MushJoints();
            for (int k = 0; k < 4; k++)
                if (Mathf.Abs(js[k] - Mathf.Clamp01(jc[k].Value)) > 1e-4f) return Fail("joint strength " + k + " is " + F(js[k], "0.00") + " in the kernel but " + F(jc[k].Value, "0.00") + " in the config");
            string joints = "shoulders " + _perJoint[0] + ", elbows " + _perJoint[1] + ", hips " + _perJoint[2] + ", knees " + _perJoint[3];
            if (badW > 0) return Fail(badW + " rest smoothing weights out of 0..1 or NaN (MushBake)");
            if (weighted < baked) return Fail((baked - weighted) + " of " + baked + " mushed renderers have no rest weights uploaded (MushAdjWBuf)");
            for (int k = 0; k < 4; k++) if (_perJoint[k] == 0) return Fail("no core vertex at the " + (k == 0 ? "shoulders" : k == 1 ? "elbows" : k == 2 ? "hips" : "knees") + " (" + joints + "): MushBake joint classes");
            string ran = gpu.MushVerts > 0 ? "ran on " + gpu.MushVerts + " verts last frame" : "did not run last frame";
            string state = core + " core verts on " + baked + " renderers (" + joints + "), " + passes + " passes, rest weights on all, strengths " +
                           F(js.x, "0.##") + "/" + F(js.y, "0.##") + "/" + F(js.z, "0.##") + "/" + F(js.w, "0.##") + ", keep volume " + F(GpuContact.MushKeepCfg.Value, "0.##") + ", " + ran +
                           "; 0 vs 1 output not measured (GPU only)";
            bool want = p.DeltaMush.Value && p.DeltaMushAmount.Value > 0.001f && ShapeTest.MushOverride == null;
            if (want && gpu.MushVerts == 0 && gpu.CulledRenderers == 0) return Fail(state + ": on and baked but not dispatched (MushOk / the render hook)");
            return Pass(state + (want && gpu.MushVerts == 0 ? " (her meshes are culled now)" : !p.DeltaMush.Value ? " (switched off)" : ""));
        }

        // Wrinkles: layouts with the 5 new body regions; forcing them to full makes their sheet re-blend.
        static readonly string[] NewBody = { "flank_L", "flank_R", "sidewaist_L", "sidewaist_R", "upperbelly" };
        static int _r0;
        static string _wSheet = "";
        static string WrinklesT(Plugin p, Body b, float t)
        {
            Character her;
            string no = NoHer(p, b, out her);
            if (no.Length > 0) return Skip(no);
            var wr = b.Wrink;
            if (wr == null) return p.WrinkOn.Value ? Fail("on but not loaded: " + b.WrinkleStatus) : Skip("wrinkles are off (Skin tab)");
            if (_phase == 0)
            {
                if (wr.Dead) return Fail("turned itself off: " + wr.Status);
                if (WrinkleTest.Index >= 0) return Skip("the Wrinkle test is running");
                string miss = "";
                foreach (var r in NewBody) if (!wr.HasRegion(r)) miss += (miss.Length > 0 ? " " : "") + r;
                if (miss.Length > 0) return Fail("the layouts (" + wr.LayoutSummary() + ") lack " + miss + ": old sfmorph_shaders");
                if (!wr.FlipOk) return Fail("the _FlipY self-test has not settled (log: 'Wrinkles: _FlipY self-test'); nothing blends until it does");
                bool culled;
                if (!wr.SheetOf("flank_L", out culled, out _wSheet)) return Fail("no body sheet built (" + wr.Status + "): body or neck wrinkles must be on and her body skin found");
                if (culled) return Skip("her body sheet '" + _wSheet + "' is culled (out of view or beyond the wrinkles' cull distance): look at her and run it again");
                _r0 = wr.SheetRasters("flank_L");
                WrinkleTest.Force(NewBody, 1.5f);
                _phase = 1;
                return null;
            }
            if (t < 0.8f) return null;
            float minAct = 1f, c;
            foreach (var r in NewBody) minAct = Mathf.Min(minAct, wr.RegionAct(r, out c));
            int n = wr.SheetRasters("flank_L") - _r0;
            WrinkleTest.Unforce();
            string nums = "layouts " + wr.LayoutSummary() + ", the 5 new body regions found; forced on sheet '" + _wSheet + "': lowest activation " + F(minAct, "0.00") + ", " + n + " re-blends in 0.8 s";
            if (minAct < 0.99f) return Fail(nums + " (UpdateActs did not take the forced regions)");
            if (n <= 0) return Fail(nums + " (no raster: the sheet is culled or the frame hooks don't run; log: 'Wrinkles: no frame hook')");
            return Pass(nums);
        }

        // Curve-driven wrinkles: browInnerUp held at 1 through FaceCurves for ~1 s must raise forehead_L's curve target and activation.
        static float _a0;
        static string Curve(Plugin p, Body b, float t)
        {
            Character her;
            string no = NoHer(p, b, out her);
            if (no.Length > 0) return Skip(no);
            var wr = b.Wrink;
            if (wr == null) return p.WrinkOn.Value ? Fail("wrinkles on but not loaded: " + b.WrinkleStatus) : Skip("wrinkles are off (Skin tab)");
            if (!p.WrinkOn.Value) return Skip("the wrinkles themselves are off (muscles only)");
            if (!p.WrinkFace.Value) return Skip("face wrinkles are off");
            if (!p.WrinkCurveDrive.Value) return Skip("[Wrinkles] CurveDrive is off");
            float gain = Mathf.Clamp(p.WrinkCurveGain.Value, 0f, 2f);
            if (gain <= 0.01f) return Skip("[Wrinkles] CurveGain is 0");
            const string region = "forehead_L";
            if (_phase == 0)
            {
                if (WrinkleTest.Index >= 0 || WrinkleTest.Forced) return Skip("a wrinkle test is running");
                bool culled; string sheet;
                if (!wr.SheetOf(region, out culled, out sheet)) return Fail("no face sheet with " + region + " is built (" + wr.Status + ")");
                int k = FaceCurves.Index("browInnerUp");
                if (k < 0) return Fail("FaceCurves has no browInnerUp curve");
                float c0;
                _a0 = wr.RegionAct(region, out c0);
                FaceCurves.Inject(k, 1f, 1.5f);
                _phase = 1;
                return null;
            }
            if (t < 1f) return null;
            float c1, a1 = wr.RegionAct(region, out c1);
            FaceCurves.ClearInjected();
            float want = Mathf.Clamp01(0.75f * gain);   // forehead term: min(0.75, (1 - 0.1) x 1.4) x CurveGain
            string nums = "browInnerUp 1 for 1 s: " + region + " curve target " + F(c1, "0.00") + " (expected 0.75), activation " + F(_a0, "0.00") + " -> " + F(a1, "0.00") +
                          " (gain " + F(gain, "0.00") + ")";
            if (c1 < 0.5f) return Fail(nums + ": the curve did not reach the region (Wrinkles.CurveTargets / FaceCurves.Read)");
            if (a1 < Mathf.Max(_a0 + 0.2f * want, 0.6f * want) && a1 < 0.95f) return Fail(nums + ": the target did not raise the activation (UpdateActs; face wrinkles on?)");
            return Pass(nums);
        }

        // Muscle lengths: every strand of the table bound, none skipped, small activations at her bind pose, the live maximum.
        static string MusclesT(Plugin p, Body b)
        {
            Character her;
            string no = NoHer(p, b, out her);
            if (no.Length > 0) return Skip(no);
            if (!p.MuscleOn.Value) return Skip("muscles are off (Skin tab)");
            if (b.Wrink == null) return Fail("muscles on but the wrinkle system holding them is not loaded: " + b.WrinkleStatus);
            if (!b.Wrink.HasMuscles) return Fail("no muscle layer in the shader bundle (old sfmorph_shaders)");
            int n = MusclePose.Strands, e = MusclePose.ExpectedStrands;
            if (n == 0) return Fail("no strand bound (" + (MusclePose.BindNote.Length > 0 ? MusclePose.BindNote : "her body sheet not built yet") + (MusclePose.SkippedNote.Length > 0 ? "; skipped " + MusclePose.SkippedNote : "") + ")");
            string worst, live;
            float bind = MusclePose.BindAct(her, out worst);
            float now = MusclePose.LiveMax(out live);
            string nums = n + " of " + e + " strands on " + MusclePose.MuscleCount + " muscles over " + MusclePose.PathBones + " bones (" + MusclePose.BindNote + "), driver " + MusclePose.Driver +
                          "; at her bind pose the strongest is " + (worst.Length > 0 ? worst + " -> " + F(bind, "0.00") : "none") + "; now " + (live.Length > 0 ? live + " " + F(now, "0.00") : "all at rest");
            if (MusclePose.SkippedStrands > 0 || n < e) return Fail(nums + "; skipped " + (MusclePose.SkippedNote.Length > 0 ? MusclePose.SkippedNote : (e - n) + " strands") + " (her rig lacks those bones)");
            if (bind > 0.35f) return Fail(nums + ": too active at rest (MusclePaths thresholds / rest lengths)");
            return Pass(nums);
        }

        // Pose correctives: both drivers built and interpolative; a target pose evaluates to weight 1 and that target's outputs, the rest
        // target to 0, a swing halfway between abd45 and abd90 to an elevation between theirs; the output signs; and with the feature off
        // no offset is written. Only RbfPose.Evaluate runs (no bone is touched); its weights / outputs are put back after.
        static float[] _wSave = new float[0], _oSave = new float[0];
        static string Correctives(Plugin p, Body b)
        {
            Character her;
            string no = NoHer(p, b, out her);
            if (no.Length > 0) return Skip(no);
            var dl = PoseDriver.Drivers[PoseDriver.Left]; var dr = PoseDriver.Drivers[PoseDriver.Right];
            if (dl == null && dr == null)
                return PoseDriver.Status.StartsWith("off", StringComparison.Ordinal) ? Fail(PoseDriver.Status + " (log: 'Pose correctives:')") : Skip("not built yet (PoseDriver.Step has not run for her)");
            var sb = new StringBuilder();
            string bad = null;
            Transform neck;
            her.Bones.TryGetValue("Neck_M", out neck);
            for (int s = 0; s < 2 && bad == null; s++)
            {
                var rb = PoseDriver.Drivers[s];
                string side = s == 0 ? "L" : "R";
                if (rb == null) { bad = side + " shoulder not built (log: 'Pose correctives: clavicle drivers')"; break; }
                if (!rb.Interpolative) { bad = side + ": additive fallback (singular targets, min pivot " + F(rb.MinPivot, "0.000") + ")"; break; }
                if (_wSave.Length < rb.N) _wSave = new float[rb.N];
                if (_oSave.Length < rb.M) _oSave = new float[rb.M];
                Array.Copy(rb.Weights, _wSave, rb.N); Array.Copy(rb.Outputs, _oSave, rb.M);
                float nearSave = rb.Nearest;
                try { bad = CheckDriver(rb, side, sb); }
                finally { Array.Copy(_wSave, rb.Weights, rb.N); Array.Copy(_oSave, rb.Outputs, rb.M); rb.Nearest = nearSave; }
                if (bad != null) break;
                float rise, fwd, back, up;
                if (PoseDriver.AxisSigns(s, neck, out rise, out fwd, out back, out up))
                {
                    if (rise <= 0f || fwd <= 0f || back <= 0f) bad = side + ": offset axes move the girdle the wrong way (rise " + F(rise, "0.00") + ", forward " + F(fwd, "0.00") + ", tilt back " + F(back, "0.00") + ")";
                    else if (neck != null && up <= 0.3f) bad = side + ": her up in Chest_M's frame points away from the neck (dot " + F(up, "0.00") + "): build frame wrong";
                    else sb.Append(", axes ok (up . neck ").Append(F(up, "0.00")).Append(')');
                }
            }
            bool on = PoseDriver.On.Value;
            if (bad == null && !on)
            {
                var o0 = PoseDriver.ScapulaOffsetDeg[0]; var o1 = PoseDriver.ScapulaOffsetDeg[1];
                if (o0.sqrMagnitude > 1e-8f || o1.sqrMagnitude > 1e-8f) bad = "the feature is off but a scapula offset is written (" + o0 + " / " + o1 + ")";
                else sb.Append("; off: no bone written");
            }
            else if (bad == null) sb.Append("; on (amount ").Append(F(PoseDriver.ClavicleAmount.Value, "0.00")).Append("), now L ").Append(PoseDriver.ScapulaOffsetDeg[0].ToString("0.0"))
                                  .Append(" R ").Append(PoseDriver.ScapulaOffsetDeg[1].ToString("0.0"));
            if (bad != null) return Fail(bad);
            return Pass(sb.ToString());
        }

        static string CheckDriver(RbfPose rb, string side, StringBuilder sb)
        {
            int rest = rb.IndexOf("rest"), a45 = rb.IndexOf("abd45"), a90 = rb.IndexOf("abd90"), a150 = rb.IndexOf("abd150"), f90 = rb.IndexOf("flex90"),
                b45 = rb.IndexOf("back45"), acr = rb.IndexOf("across");
            if (rest != 0) return side + ": no rest target first";
            if (a90 < 0) return side + ": no abd90 target (dropped as too close to another?)";
            // exact at a target
            rb.Evaluate(rb.Targets[a90]);
            float w = rb.Weights[a90];
            float de = Mathf.Abs(rb.Outputs[0] - rb.Out[a90, 0]), dp = Mathf.Abs(rb.Outputs[1] - rb.Out[a90, 1]), dt = Mathf.Abs(rb.Outputs[2] - rb.Out[a90, 2]);
            if (w < 0.95f || de > 0.5f || dp > 0.5f || dt > 0.5f)
                return side + ": at abd90 weight " + F(w, "0.00") + ", outputs " + F(rb.Outputs[0], "0.0") + "/" + F(rb.Outputs[1], "0.0") + "/" + F(rb.Outputs[2], "0.0") +
                       " vs table " + F(rb.Out[a90, 0], "0.0") + "/" + F(rb.Out[a90, 1], "0.0") + "/" + F(rb.Out[a90, 2], "0.0") + " (RbfPose.Solve)";
            sb.Append(sb.Length > 0 ? "; " : "").Append(side).Append(": ").Append(rb.N).Append(" targets, radius ").Append(F(rb.Radius, "0")).Append(", interpolative (pivot ")
              .Append(F(rb.MinPivot, "0.000")).Append("), abd90 w ").Append(F(w, "0.00")).Append(" -> elev ").Append(F(rb.Outputs[0], "0.0")).Append(" prot ").Append(F(rb.Outputs[1], "0.0"))
              .Append(" tilt ").Append(F(rb.Outputs[2], "0.0"));
            // the rest pose gives nothing
            rb.Evaluate(rb.Targets[rest]);
            if (Mathf.Abs(rb.Outputs[0]) > 0.5f || Mathf.Abs(rb.Outputs[1]) > 0.5f || Mathf.Abs(rb.Outputs[2]) > 0.5f)
                return side + ": the rest pose gives " + F(rb.Outputs[0], "0.0") + "/" + F(rb.Outputs[1], "0.0") + "/" + F(rb.Outputs[2], "0.0") + " (should be 0)";
            // a synthetic swing halfway between abd45 and abd90
            if (a45 >= 0)
            {
                rb.Evaluate(Quaternion.Slerp(rb.Targets[a45], rb.Targets[a90], 0.5f));
                float lo = Mathf.Min(rb.Out[a45, 0], rb.Out[a90, 0]) - 2f, hi = Mathf.Max(rb.Out[a45, 0], rb.Out[a90, 0]) + 2f;
                if (rb.Outputs[0] < lo || rb.Outputs[0] > hi) return side + ": halfway abd45 -> abd90 the elevation is " + F(rb.Outputs[0], "0.0") + ", outside " + F(lo, "0.0") + ".." + F(hi, "0.0");
                sb.Append(", abd67 -> elev ").Append(F(rb.Outputs[0], "0.0"));
            }
            // the table's signs: up and back-tilt with a raised arm, forward when reaching forward / across, back when reaching back
            if (a150 >= 0 && (rb.Out[a150, 0] <= 0f || rb.Out[a150, 2] <= 0f)) return side + ": abd150 does not raise / tilt the shoulder (table)";
            if (f90 >= 0 && rb.Out[f90, 1] <= 0f) return side + ": flex90 does not bring the shoulder forward (table)";
            if (acr >= 0 && rb.Out[acr, 1] <= 0f) return side + ": across does not bring the shoulder forward (table)";
            if (b45 >= 0 && rb.Out[b45, 1] >= 0f) return side + ": back45 does not take the shoulder back (table)";
            sb.Append(", signs ok");
            return null;
        }

        // Quality presets: which one, how many covered settings differ; re-applying the picked one (only when nothing differs) writes nothing.
        static string QualityT(Plugin p)
        {
            int cur = Mathf.Clamp(p.QualityPreset.Value, 0, QualityPresets.Count - 1);
            int diff = QualityPresets.Differences(cur);
            string name = QualityPresets.Names[cur];
            if (QualityPresets.KnobCount == 0) return Fail("no covered settings (QualityPresets.Init did not run)");
            if (diff > 0) return Skip("Custom: " + diff + " of " + QualityPresets.KnobCount + " settings differ from " + name + " (the no-op check would write them back): " +
                                      QualityPresets.DiffText(cur).Replace("\n", ";").Trim());
            QualityPresets.LastWritten = -1;
            QualityPresets.Apply(p, cur, "smoke test");
            int written = QualityPresets.LastWritten, after = QualityPresets.Differences(cur);
            string nums = name + ", " + QualityPresets.KnobCount + " settings covered, 0 differ; re-applying it wrote " + written + ", " + after + " differ after";
            if (written != 0 || after != 0 || p.QualityPreset.Value != cur) return Fail(nums + " (Knob.Matches vs Apply)");
            return Pass(nums);
        }

        // Lip sync: the curves there are; LipFilter on a synthetic window (a one-frame mouthClose spike between two 23 fps frames).
        static float[] _lw;
        static string Lip(Plugin p, Body b)
        {
            int K = LipSync.K;
            if (_lw == null || _lw.Length < K) _lw = new float[K];
            int mc = LipSync.MouthClose, jaw = LipSync.JawOpen;
            var c = new LipSync.Curve { Frames = 12, Fps = 60f, W = new ushort[12 * K], Seconds = 0.2f };
            c.W[6 * K + mc] = 65535;
            c.W[6 * K + jaw] = 65535;   // a non-closure channel with the same spike: averaged by filter 2, never peaked
            float t = 7.5f / 60f, win = 1f / 23f;   // the frame [4.9, 7.5] in curve frames: holds frame 6, ends between 7 and 8
            c.SampleWindow(t, win, 0, _lw); float f0 = _lw[mc];
            c.SampleWindow(t, win, 1, _lw); float f1 = _lw[mc];
            c.SampleWindow(t, win, 2, _lw); float f2 = _lw[mc], j2 = _lw[jaw];
            string syn = "synthetic 1-frame closure in a 43 ms frame: moment " + F(f0, "0.00") + ", average " + F(f1, "0.00") + ", average + closures " + F(f2, "0.00") + " (jaw " + F(j2, "0.00") + ")";
            if (f2 < 0.99f || f0 > 0.01f || f1 > 0.6f || f1 < 0.2f || Mathf.Abs(j2 - f1) > 0.01f) return Fail(syn + ": LipSync.Curve.SampleWindow");
            string pack = ReactionsGame.Pack.Loaded ? ReactionsGame.Pack.Name : "";
            int pc = pack.Length > 0 ? LipSync.PackCurves(pack) : -1, gc = LipSync.GameCurves;
            string set = "curves: " + (pack.Length > 0 ? "pack '" + pack + "' " + Math.Max(0, pc) : "no pack") + ", game clips " + gc + "; map " + LipSync.PosedShapes + " of " + K + " shapes; filter " + p.LipFilter.Value +
                         (p.LipAutoLead.Value ? ", lead a frame" : "");
            if (!p.LipSyncOn.Value) return Pass(syn + "; " + set + " (lip sync is off)");
            if (pc <= 0 && gc <= 0) return Fail(syn + "; " + set + ": no curves baked (tools/a2f into " + LipSync.Dir + ")");
            if (LipSync.PosedShapes == 0) return Fail(syn + "; " + set + ": no mouth pose mapped (her face survey: " + b.React.Face.Status + ")");
            return Pass(syn + "; " + set);
        }

        // Soft zones: helpers bound; over ~1 s the springs integrated the frame time (0.11.48: equal substeps adding up to each frame, SimClock)
        // and no simulation time was dropped (only a frame over 0.1 s may drop some).
        static int _sf0; static long _ss0; static double _sd0, _sx0, _st0;
        static string Soft(Plugin p, Body b, float t)
        {
            Character her;
            string no = NoHer(p, b, out her);
            if (no.Length > 0) return Skip(no);
            if (!p.Physics.Value) return Skip("soft zones are off (Physics tab)");
            var z = her.Soft;
            if (z == null || z.HelperCount == 0) return Fail("no helper bound (" + (z != null ? z.Status : "no soft zones") + ")");
            if (_phase == 0) { _sf0 = z.SimFrames; _ss0 = z.SimSteps; _sd0 = z.SimDt; _sx0 = z.SimDropped; _st0 = z.SimTime; z.SimMaxSteps = 0; _phase = 1; return null; }
            if (t < 1f) return null;
            int frames = z.SimFrames - _sf0;
            if (frames <= 0) return Fail(z.HelperCount + " helpers but Simulate did not run in 1 s (physics on? her soft zones built?)");
            long steps = z.SimSteps - _ss0;
            double dt = z.SimDt - _sd0, dropped = z.SimDropped - _sx0, sim = z.SimTime - _st0;
            float fps = (float)(frames / Math.Max(1e-4, dt));
            string nums = z.HelperCount + " helpers; " + frames + " frames at " + F(fps, "0") + " fps, " + F((float)steps / frames, "0.0") + " steps / frame (most " + z.SimMaxSteps + ", cap " +
                          SoftZones.MaxSteps + "), simulated " + F((float)sim, "0.000") + " of " + F((float)dt, "0.000") + " s, dropped " + F((float)dropped, "0.000") + " s";
            if (dropped > 1e-4)
                return Fail(nums + ": a hitch (a frame over 0.1 s) dropped it; run again");
            if (Math.Abs(sim + dropped - dt) > 1e-3 * dt + 1e-4)
                return Fail(nums + ": the springs' time does not add up to the frame time (SimClock substeps broken)");
            return Pass(nums);
        }

        static string Hands(Plugin p)
        {
            if (!p.HandsOn.Value) return Skip("physics hands are off (VR tab)");
            if (!PhysicsHands.Active) return Fail("on but not built: " + PhysicsHands.Status);
            int n = PhysicsHands.Capsules.Count;
            string nums = n + " capsules, hulls " + (p.HandsHull.Value ? "on" : "off") + ", jointed fingers " + (p.HandsJointed.Value ? "on" : "off") + ", breast colliders " + p.BreastColliders.Value +
                          ", " + PhysicsHands.Status;
            if (n == 0) return Fail(nums + ": no capsule for the soft zones / GPU contact");
            return Pass(nums);
        }

        // Skin marks are frozen (docs/LESSONS.md): only whether they are alive and their orientation self-test settled.
        // 0.11.48 frame timing: the soft-zone clock's self-test (SimClock.SelfTest, the same code tools/timing's harness checks offline: the
        // springs move the same at 90, 41 and uneven 23 fps and advance by exactly the frame time) and the Perf line's frame phase markers.
        static string Timing(Plugin p)
        {
            string rep;
            bool clock = SimClock.SelfTest(out rep);
            bool marks = !p.PerfLog.Value || Perf.LoopOk;
            string nums = "clock " + rep + ", now " + (Time.unscaledDeltaTime > 0f ? F(1f / Time.unscaledDeltaTime, "0") : "-") + " fps, soft-zone steps cap " + SoftZones.MaxSteps
                          + (p.PerfLog.Value ? ", phase markers " + (Perf.LoopOk ? "in the PlayerLoop" : "missing") : ", PerfLog off");
            if (!clock) return Fail(nums + ": the clock depends on the frame rate (see tools/timing)");
            if (!marks) return Fail(nums + ": the Perf line has no frame phases");
            return Pass(nums);
        }

        static string Marks(Plugin p, Body b)
        {
            Character her;
            string no = NoHer(p, b, out her);
            if (no.Length > 0) return Skip(no);
            if (!p.SkinMarksOn.Value) return Skip("skin marks are off (Physics tab)");
            string off = GpuOff(p, b);
            if (off == null) return Fail("GPU contact on but not loaded: " + b.GpuStatus);
            if (off.Length > 0) return Skip("marks need GPU contact: " + off);
            var m = b.Marks;
            if (m.Dead) return Fail("turned themselves off: " + m.Status + " (log: 'Skin marks:')");
            if (!m.FlipSettled) return Fail("the _FlipY self-test has not settled (" + m.Status + "; log: 'Skin marks: _FlipY self-test')");
            if (m.Status.Contains("off:")) return Fail(m.Status);
            return Pass("alive, flip test settled, " + m.Updates + " updates; " + (m.Status.Length > 0 ? m.Status : "idle"));
        }
    }
}
