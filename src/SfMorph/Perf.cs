using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace SfMorph
{
    // 0.9.9 timing: what SF Body Morphs costs per frame, and where the rest of the frame goes. A Stopwatch timestamp around each stage
    // (about 30 ns a pair, ~40 pairs a frame), summed per frame, then every 10 s one log line with avg/max ms per stage, the frame rate
    // and the fixed steps per frame. Markers at the start and end of each of Unity's frame phases (PlayerLoop) time the game's own CPU
    // work the same way (PhysX = FixedUpdate.PhysicsFixedUpdate; HDRP's render submit = PostLateUpdate), and the gap between the end of
    // one frame and the start of the next is time the main thread spent waiting (for the GPU / the VR compositor). The GPU time of the
    // frame comes from the XR display (app GPU ms, when the runtime reports it), else from FrameTimingManager, else it is left out.
    // CPU only for our own stages: the GPU time of our dispatches is inside the frame's GPU time (A/B with the toggles to see it).
    internal static class Perf
    {
        public enum S
        {
            Total, Hands, Grip, FaceClip, Morph, SoftUpdate, Trackers, HerBody, Overlap, PhysHands, Reactions, Wrinkles, Embody,
            Simulate, Ripple, GpuSync, Marks, Mirror,
            HandsFixed, GpuRun, WrinkleRun, MarksRaster, Panel,
            // Unity's phases (the game's work and ours together)
            PhInit, PhEarly, PhFixed, PhPhysX, PhPre, PhUpdate, PhPreLate, PhPostLate, PhWait,
            Count
        }

        static readonly string[] Names =
        {
            "applyAll", "vrHands", "grip", "faceClip", "morph", "softUpd", "trackers", "herBody", "overlap", "physHands", "react", "wrinkles", "embody",
            "simulate", "ripple", "gpuSync", "marks", "mirror",
            "handsFixed", "gpuRun", "wrinkleRun", "marksRaster", "panel",
            "init", "early", "fixed", "physx", "pre", "update", "preLate", "postLate", "wait",
        };

        public static bool On = true;
        public static string Summary = "";            // the last 10 s line, for the panel
        static readonly long[] _t0 = new long[(int)S.Count];
        static readonly long[] _cur = new long[(int)S.Count];
        static readonly long[] _sum = new long[(int)S.Count];
        static readonly long[] _max = new long[(int)S.Count];
        static readonly double _msPerTick = 1000.0 / Stopwatch.Frequency;
        static int _frames, _steps, _stepsNow, _stepsMax, _lastRoll = -1;
        static double _dtSum, _dtMax;
        static double _gpuSum, _gpuMax; static int _gpuN;
        static long _frameEnd;                         // end of the last PostLateUpdate (for the wait gap)
        static float _next;
        static readonly StringBuilder _sb = new StringBuilder(1024);

        public static void Begin(S s) { if (On) _t0[(int)s] = Stopwatch.GetTimestamp(); }
        public static void End(S s) { if (On) _cur[(int)s] += Stopwatch.GetTimestamp() - _t0[(int)s]; }
        public static void Step() { if (On) _stepsNow++; }

        // Once per frame, at the start of the frame (Initialization marker) or, without the markers, at Runner.Update: the last frame's
        // sums go into the window; every 10 s the line.
        public static void Roll()
        {
            if (!On) return;
            int f = Time.frameCount;
            if (f == _lastRoll) return;
            _lastRoll = f;
            if (_frameEnd != 0) { _cur[(int)S.PhWait] += Stopwatch.GetTimestamp() - _frameEnd; _frameEnd = 0; }
            for (int i = 0; i < _cur.Length; i++)
            {
                long v = _cur[i];
                _sum[i] += v;
                if (v > _max[i]) _max[i] = v;
                _cur[i] = 0;
            }
            _steps += _stepsNow; if (_stepsNow > _stepsMax) _stepsMax = _stepsNow; _stepsNow = 0;
            double dt = Time.unscaledDeltaTime * 1000.0;
            _dtSum += dt; if (dt > _dtMax) _dtMax = dt;
            float g;
            if (GpuMs(out g)) { _gpuSum += g; if (g > _gpuMax) _gpuMax = g; _gpuN++; }
            _frames++;
            float now = Time.unscaledTime;
            if (_next == 0f) { _next = now + 10f; Reset(); return; }
            if (now < _next) return;
            _next = now + 10f;
            Write();
            Reset();
        }

        static void Reset()
        {
            Array.Clear(_sum, 0, _sum.Length); Array.Clear(_max, 0, _max.Length);
            _frames = 0; _steps = 0; _stepsMax = 0; _dtSum = 0; _dtMax = 0; _gpuSum = 0; _gpuMax = 0; _gpuN = 0;
        }

        static void Write()
        {
            if (_frames == 0) return;
            var sb = _sb; sb.Length = 0;
            double n = _frames;
            double dtAvg = _dtSum / n;
            sb.Append("Perf last 10 s: ").Append((1000.0 / Math.Max(dtAvg, 0.01)).ToString("0.0")).Append(" fps (frame avg ").Append(dtAvg.ToString("0.0"))
              .Append(" max ").Append(_dtMax.ToString("0")).Append(" ms), fixed steps/frame avg ").Append((_steps / n).ToString("0.0")).Append(" max ").Append(_stepsMax);
            if (_gpuN > 0) sb.Append(", GPU ").Append((_gpuSum / _gpuN).ToString("0.0")).Append(" max ").Append(_gpuMax.ToString("0.0")).Append(" ms (").Append(_gpuSource).Append(')');
            else sb.Append(", GPU time not reported");
            sb.Append("\n  ours ms avg/max: ");
            Stages(sb, (int)S.Total, (int)S.Panel, n);
            sb.Append("\n  frame phases ms avg/max (game + ours): ");
            Stages(sb, (int)S.PhInit, (int)S.PhWait, n);
            if (!_loopOk) sb.Append(" (no phase markers: ").Append(_loopNote).Append(')');
            Summary = sb.ToString();
            Plugin.Log.LogInfo(Summary);
        }

        static void Stages(StringBuilder sb, int from, int to, double n)
        {
            bool first = true;
            for (int i = from; i <= to; i++)
            {
                if (_max[i] == 0) continue;   // never ran in the window (off)
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(Names[i]).Append(' ').Append((_sum[i] * _msPerTick / n).ToString("0.00")).Append('/').Append((_max[i] * _msPerTick).ToString("0.0"));
            }
            if (first) sb.Append('-');
        }

        // ---- frame phase markers (PlayerLoop) ----

        struct PerfMark { }
        static bool _loopOk;
        static string _loopNote = "not tried";
        static readonly Dictionary<Type, PlayerLoopSystem.UpdateFunction[]> _marks = new Dictionary<Type, PlayerLoopSystem.UpdateFunction[]>();

        // Every 2 s with ApplyStep.Ensure (another mod may set the loop again): markers at the start and end of each top-level phase, and
        // around FixedUpdate.PhysicsFixedUpdate. The FixedUpdate phase runs once per fixed step, so its markers sum over the steps.
        public static void EnsureLoop()
        {
            if (!On) return;
            try
            {
                var root = PlayerLoop.GetCurrentPlayerLoop();
                var subs = root.subSystemList;
                if (subs == null) { _loopOk = false; _loopNote = "empty loop"; return; }
                bool changed = false;
                for (int i = 0; i < subs.Length; i++)
                {
                    var t = subs[i].type;
                    S ph;
                    if (t == typeof(Initialization)) ph = S.PhInit;
                    else if (t == typeof(EarlyUpdate)) ph = S.PhEarly;
                    else if (t == typeof(FixedUpdate)) ph = S.PhFixed;
                    else if (t == typeof(PreUpdate)) ph = S.PhPre;
                    else if (t == typeof(Update)) ph = S.PhUpdate;
                    else if (t == typeof(PreLateUpdate)) ph = S.PhPreLate;
                    else if (t == typeof(PostLateUpdate)) ph = S.PhPostLate;
                    else continue;
                    if (subs[i].subSystemList == null) continue;
                    var list = new List<PlayerLoopSystem>(subs[i].subSystemList);
                    if (list.Exists(s => s.type == typeof(PerfMark))) continue;
                    var fn = Fns(t, ph);
                    list.Insert(0, new PlayerLoopSystem { type = typeof(PerfMark), updateDelegate = fn[0] });
                    list.Add(new PlayerLoopSystem { type = typeof(PerfMark), updateDelegate = fn[1] });
                    if (ph == S.PhFixed)
                    {
                        int at = list.FindIndex(s => s.type == typeof(FixedUpdate.PhysicsFixedUpdate));
                        if (at > 0)
                        {
                            list.Insert(at + 1, new PlayerLoopSystem { type = typeof(PerfMark), updateDelegate = fn[3] });
                            list.Insert(at, new PlayerLoopSystem { type = typeof(PerfMark), updateDelegate = fn[2] });
                        }
                    }
                    subs[i].subSystemList = list.ToArray();
                    changed = true;
                }
                if (changed) { root.subSystemList = subs; PlayerLoop.SetPlayerLoop(root); }
                _loopOk = true; _loopNote = "";
            }
            catch (Exception e) { _loopOk = false; _loopNote = e.GetType().Name; }
        }

        // The marker delegates of a phase, made once (no allocation per frame).
        static PlayerLoopSystem.UpdateFunction[] Fns(Type t, S ph)
        {
            PlayerLoopSystem.UpdateFunction[] fn;
            if (_marks.TryGetValue(t, out fn)) return fn;
            int i = (int)ph;
            fn = new PlayerLoopSystem.UpdateFunction[4];
            if (ph == S.PhInit) fn[0] = () => { Roll(); if (On) _t0[i] = Stopwatch.GetTimestamp(); };
            else if (ph == S.PhFixed) fn[0] = () => { if (On) { _t0[i] = Stopwatch.GetTimestamp(); _stepsNow++; } };
            else fn[0] = () => { if (On) _t0[i] = Stopwatch.GetTimestamp(); };
            if (ph == S.PhPostLate) fn[1] = () => { if (On) { long now = Stopwatch.GetTimestamp(); _cur[i] += now - _t0[i]; _frameEnd = now; } };
            else fn[1] = () => { if (On) _cur[i] += Stopwatch.GetTimestamp() - _t0[i]; };
            int px = (int)S.PhPhysX;
            fn[2] = () => { if (On) _t0[px] = Stopwatch.GetTimestamp(); };
            fn[3] = () => { if (On) _cur[px] += Stopwatch.GetTimestamp() - _t0[px]; };
            _marks[t] = fn;
            return fn;
        }

        // Without the markers the fixed steps are counted by Runner.FixedUpdate instead.
        public static bool LoopOk { get { return _loopOk; } }

        // ---- the frame's GPU time ----

        delegate bool TryFloat(out float v);
        static TryFloat _xrGpu;
        static float _nextXr;
        static string _gpuSource = "";
        static readonly FrameTiming[] _ft = new FrameTiming[1];
        static bool _ftDead;

        static bool GpuMs(out float ms)
        {
            ms = 0f;
            if (_xrGpu == null && Time.unscaledTime >= _nextXr) { _nextXr = Time.unscaledTime + 10f; FindXr(); }
            if (_xrGpu != null)
            {
                try { if (_xrGpu(out ms) && ms > 0f) { _gpuSource = "XR app GPU"; return true; } }
                catch { _xrGpu = null; }
            }
            if (_ftDead) return false;
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, _ft) > 0 && _ft[0].gpuFrameTime > 0.0)
                {
                    ms = (float)_ft[0].gpuFrameTime; _gpuSource = "frame timing"; return true;
                }
            }
            catch { _ftDead = true; }
            return false;
        }

        // XRDisplaySubsystem.TryGetAppGPUTimeLastFrame of the running display, by reflection (UnityEngine.XRModule isn't referenced).
        // Looked up every 10 s until found (the display starts after the plugin).
        static void FindXr()
        {
            try
            {
                var dispT = Type.GetType("UnityEngine.XR.XRDisplaySubsystem, UnityEngine.XRModule");
                var mgrT = Type.GetType("UnityEngine.SubsystemManager, UnityEngine.SubsystemsModule") ?? Type.GetType("UnityEngine.SubsystemManager, UnityEngine.CoreModule");
                if (dispT == null || mgrT == null) return;
                MethodInfo get = null;
                foreach (var m in mgrT.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "GetInstances" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1) { get = m; break; }
                if (get == null) return;
                var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(dispT));
                get.MakeGenericMethod(dispT).Invoke(null, new[] { list });
                var mi = dispT.GetMethod("TryGetAppGPUTimeLastFrame", BindingFlags.Public | BindingFlags.Instance);
                var running = dispT.GetProperty("running", BindingFlags.Public | BindingFlags.Instance);
                if (mi == null) return;
                foreach (var d in (System.Collections.IEnumerable)list)
                {
                    if (running != null && !(bool)running.GetValue(d, null)) continue;
                    _xrGpu = (TryFloat)Delegate.CreateDelegate(typeof(TryFloat), d, mi);
                    break;
                }
            }
            catch { }
        }
    }
}
