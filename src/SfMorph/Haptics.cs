using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SfMorph
{
    // 0.11.26 controller haptics from what your hands touch on her. Per hand three actuators, as on the Quest Pro's TruTouch controllers:
    //   grip    = the main motor (palm, middle / ring / little fingers, the controller itself): grabs, gropes, palm slaps, rubs;
    //   trigger = the index fingertip: its touches, rubs and going into her (vagina / anus / mouth), its strokes;
    //   thumb   = the thumb's touches.
    // Inputs: HerBody.Feed calls Contact() for every capsule of YOUR hands (hand >= 0) that is inside one of her segments this frame (hand
    // models, physics-hand ghosts at the controller pose, the embodied partner's hands at your controllers); Penetration's openings; the
    // grab button (Grip). Step() turns them into a continuous level per actuator (a faint hold by depth, a low buzz by slide speed, a
    // squeeze while the hand is closed on her, a buzz while a finger is in her) plus one-shot pulses (the touch onset by approach speed: a
    // tick, a pat or a sharp slap pulse; the grab closing; going in / out and each in / out stroke).
    // Output, found once and logged (the 'Haptics:' lines), re-checked every 5 s:
    //   1 OpenXR localized: the runtime enabled XR_FB_touch_controller_pro and Input System actions bind to the QuestProTouchController's
    //     haptic / hapticTrigger / hapticThumb (Meta's own runtime over Link). Each actuator gets its own impulses.
    //   2 OpenXR one motor: an action on <XRController>{hand}/haptic (Virtual Desktop's VDXR: it has no Touch Pro profile, and drops
    //     trigger / thumb outputs even when bound). The three actuators are mixed (the largest wins).
    //   3 UnityEngine.XR.InputDevice.SendHapticImpulse on channel 0 (any XR provider), the same mix.
    // Everything is by reflection (Unity.InputSystem / Unity.XR.OpenXR / UnityEngine.XRModule are not referenced). Rate-limited: a
    // continuous level is re-sent at most 30 times a second per channel as a 0.1 s impulse (it dies on its own if updates stop), and
    // stopped explicitly when it falls to 0; onsets are at least 0.12 s apart per actuator, strokes need a 12 mm turn.
    internal static class Haptics
    {
        public const int AGrip = 0, ATrigger = 1, AThumb = 2, NA = 3;
        public static readonly string[] ActNames = { "grip", "trigger", "thumb" };
        static readonly string[] HandNames = { "left", "right" };

        // ---- settings (Body fills them every frame from [Haptics]) ----
        public static bool On;
        public static float Strength = 1f, TouchAmt = 0.5f, ImpactAmt = 1f, GrabAmt = 0.6f, PenAmt = 0.7f;
        public static int PathPick;          // 0 auto, 1 the XR device only (channel 0)
        public static int Log = 1;           // 0 off, 1 path + first of each kind + a 10 s summary, 2 every pulse and every buzz on / off

        public static string Status = "";    // the panel's line: the path, or why nothing is sent
        public static string Path = "";      // the chosen path, short
        public static bool Localized { get { return _kind == 1; } }

        // ---- the contacts of this frame (HerBody.Feed -> Contact) ----
        struct Touch { public float Pen; public Vector3 Pt, N; public Transform Seg; }
        static readonly Touch[] _t = new Touch[2 * NA];
        static int _tFrame = -1;

        // Which actuator a hand capsule's bone stands for, by its name (cached per transform)
        static readonly Dictionary<int, int> _partOf = new Dictionary<int, int>();
        public static int PartOf(Transform t)
        {
            if (ReferenceEquals(t, null) || t == null) return AGrip;
            int id = t.GetInstanceID(), p;
            if (_partOf.TryGetValue(id, out p)) return p;
            var n = t.name.ToLowerInvariant();
            p = n.Contains("thumb") ? AThumb : n.Contains("index") || n.Contains("pointer") ? ATrigger : AGrip;
            if (_partOf.Count > 4096) _partOf.Clear();
            _partOf[id] = p;
            return p;
        }

        public static bool Want { get { return On; } }

        // HerBody.GeoPush calls this once per frame before the first Contact
        public static void Begin()
        {
            _tFrame = Time.frameCount;
            for (int i = 0; i < _t.Length; i++) _t[i] = default(Touch);
        }

        // One capsule of your hand `hand` (0 left, 1 right), actuator `part`, `pen` metres inside her segment `seg` at `pt` (n: into her)
        public static void Contact(int hand, int part, float pen, Vector3 pt, Vector3 n, Transform seg)
        {
            if (hand < 0 || hand > 1 || part < 0 || part >= NA || _tFrame != Time.frameCount) return;
            int i = hand * NA + part;
            if (pen > _t[i].Pen) { _t[i].Pen = pen; _t[i].Pt = pt; _t[i].N = n; _t[i].Seg = seg; }
        }

        // ---- per hand state ----
        sealed class Hand
        {
            public readonly bool[] Was = new bool[NA];
            public readonly float[] OnsetT = { -100f, -100f, -100f };
            public readonly float[] Cont = new float[NA];
            public readonly float[] PulseAmp = new float[NA], PulseUntil = new float[NA];
            public readonly bool[] PulseNew = new bool[NA];
            public Transform SlSeg; public Vector3 SlL; public float Slide;
            public Vector3 CtlP; public bool CtlOk; public Vector3 CtlV;
            public bool GrabWas;
            public float TouchSec;
            // output channels (localized: one per actuator; else only [0] is used)
            public readonly float[] NextSend = new float[NA], SentUntil = new float[NA], SentAmp = new float[NA];
            public readonly bool[] Buzz = new bool[NA];
        }
        static readonly Hand[] _h = { new Hand(), new Hand() };

        // penetration strokes per opening (our own small detector: Reactions' runs only while the reactions are on)
        struct Pen { public bool In; public float D, Ext; public int Dir, Owner; public float LastT; }
        static readonly Pen[] _pen = new Pen[Penetration.NO];

        // test pulses from the Debug tab
        static float _testUntil; static int _testHand = -1, _testAct = -1; static float _testAmp;
        static float _rubTestUntil, _strokeTestNext, _strokeTestUntil;

        // summary
        static int _nPulse, _nSend; static float _nextSum;
        static readonly HashSet<string> _first = new HashSet<string>();
        static bool _wasOn;

        static void L(string m) { Plugin.Log.LogInfo("Haptics: " + m); }

        // ================= the step =================

        public static void Step(float dt)
        {
            float now = Time.unscaledTime;
            Probe(now);                                   // also while off: the path is logged at startup
            bool test = Testing;
            if (!On && !test)
            {
                if (_wasOn) { _wasOn = false; StopAll(); }
                Status = Path.Length > 0 ? "off (path: " + Path + ")" : "off";
                return;
            }
            _wasOn = true;
            if (_kind == 0) { Status = "no haptics path found yet (" + (_why.Length > 0 ? _why : "looking") + ")"; return; }
            bool fresh = On && _tFrame == Time.frameCount;  // off: only the Debug tab's tests play
            try { Grip.Update(); } catch (Exception) { }
            if (On) PenAll(now);
            for (int hi = 0; hi < 2; hi++) StepHand(hi, _h[hi], fresh, dt, now);
            for (int hi = 0; hi < 2; hi++) Output(hi, _h[hi], now);
            if (Log >= 1 && now >= _nextSum)
            {
                if (_nextSum > 0f && (_nPulse > 0 || _nSend > 0))
                    L("last 10 s: " + _nPulse + " pulses, " + _nSend + " impulses sent; touching left " + _h[0].TouchSec.ToString("0.0") + " s, right " + _h[1].TouchSec.ToString("0.0") + " s (" + Path + ")");
                _nextSum = now + 10f; _nPulse = 0; _nSend = 0; _h[0].TouchSec = 0f; _h[1].TouchSec = 0f;
            }
            Status = Path + (fresh ? "" : "; her body is not built (soft zones or physics hands): only the tests");
        }

        static void Pulse(int hi, int a, float amp, float dur, string why, float now)
        {
            amp = Mathf.Clamp01(amp);
            if (amp < 0.01f || dur <= 0f) return;
            var h = _h[hi];
            if (now < h.PulseUntil[a] && h.PulseAmp[a] >= amp) return;   // a stronger pulse is still playing
            h.PulseAmp[a] = amp; h.PulseUntil[a] = now + dur; h.PulseNew[a] = true;
            _nPulse++;
            string key = why.Split(' ')[0];
            if (Log >= 2 || (Log == 1 && _first.Add(key)))
                L(HandNames[hi] + " " + ActNames[a] + " pulse " + (amp * Strength).ToString("0.00") + " for " + (dur * 1000f).ToString("0") + " ms: " + why
                  + (Localized ? "" : " (one motor)") + (Log == 1 ? " (first of its kind; Log 2 logs every one)" : ""));
        }

        static void StepHand(int hi, Hand h, bool fresh, float dt, float now)
        {
            // the controller's velocity (the approach speed of a touch)
            var ctl = hi == 0 ? VrHands.LeftController : VrHands.RightController;
            if (ctl != null && dt > 1e-4f)
            {
                Vector3 p = ctl.position;
                if (h.CtlOk)
                {
                    Vector3 v = (p - h.CtlP) / dt;
                    if (v.sqrMagnitude < 64f) h.CtlV = Vector3.Lerp(h.CtlV, v, Mathf.Clamp01(dt / 0.03f));   // >= 8 m/s: the rig jumped
                }
                h.CtlP = p; h.CtlOk = true;
            }
            else h.CtlOk = false;

            // deepest contact of the hand -> the slide speed over her skin, in her segment's own frame (her animation doesn't count)
            int deep = -1; float dpen = 0f;
            for (int a = 0; a < NA; a++) { var t = fresh ? _t[hi * NA + a] : default(Touch); if (t.Pen > dpen) { dpen = t.Pen; deep = a; } }
            bool touching = deep >= 0;
            if (touching)
            {
                h.TouchSec += dt;
                var t = _t[hi * NA + deep];
                if (t.Seg != null)
                {
                    Vector3 lp = t.Seg.InverseTransformPoint(t.Pt);
                    if (ReferenceEquals(h.SlSeg, t.Seg) && dt > 1e-4f)
                    {
                        float sp = t.Seg.TransformVector(lp - h.SlL).magnitude / dt;
                        if (sp < 3f) h.Slide += (sp - h.Slide) * Mathf.Clamp01(dt / 0.08f);   // a jump of the contact point is not a slide
                    }
                    else h.Slide = 0f;
                    h.SlSeg = t.Seg; h.SlL = lp;
                }
            }
            else { h.SlSeg = null; h.Slide = Mathf.MoveTowards(h.Slide, 0f, dt * 4f); }

            float rub = Mathf.Clamp01((h.Slide - 0.04f) / 0.36f);    // from 4 cm/s, full at 40 cm/s (the rub sound's curve)
            for (int a = 0; a < NA; a++)
            {
                var t = fresh ? _t[hi * NA + a] : default(Touch);
                bool on = t.Pen > 0f;
                if (on && !h.Was[a] && now - h.OnsetT[a] >= 0.12f)
                {
                    h.OnsetT[a] = now;
                    float vn = Mathf.Max(0f, Vector3.Dot(h.CtlV, t.N));
                    if (vn >= 0.45f)       // a pat from 0.45 m/s, a slap from 1.2, full at 3
                    {
                        float I = Mathf.Clamp01((vn - 0.45f) / 2.55f);
                        Pulse(hi, a, ImpactAmt * Mathf.Lerp(0.4f, 1f, I), Mathf.Lerp(0.03f, 0.07f, I), (vn >= 1.2f ? "slap" : "pat") + " at " + vn.ToString("0.00") + " m/s", now);
                    }
                    else Pulse(hi, a, TouchAmt * 0.45f, 0.02f, "touch onset (" + vn.ToString("0.00") + " m/s)", now);
                }
                h.Was[a] = on;
                float c = 0f;
                if (on)
                {
                    float press = Mathf.Clamp01(t.Pen / 0.02f);
                    c = Mathf.Max(TouchAmt * 0.2f * press, TouchAmt * 0.6f * rub * Mathf.Lerp(0.5f, 1f, press));
                }
                h.Cont[a] = c;
            }

            // the grab: the grab button closing while the hand touches her, then a squeeze on the grip actuator while it stays closed
            float gv = hi == 0 ? Grip.GrabLeft : Grip.GrabRight;
            bool closed = On && (h.GrabWas ? gv > 0.35f : gv >= 0.6f);
            if (closed && !h.GrabWas && touching) Pulse(hi, AGrip, GrabAmt * Mathf.Lerp(0.6f, 1f, gv), 0.08f, "grab (button " + gv.ToString("0.00") + ")", now);
            h.GrabWas = closed;
            if (closed && touching) h.Cont[AGrip] = Mathf.Max(h.Cont[AGrip], GrabAmt * 0.3f * Mathf.Clamp01(gv) * Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(dpen / 0.02f)));

            // fingers in her (PenAll): a buzz on the trigger actuator by depth
            for (int k = 0; k < Penetration.NO; k++)
                if (_pen[k].In && _pen[k].Owner == hi)
                    h.Cont[ATrigger] = Mathf.Max(h.Cont[ATrigger], PenAmt * (0.15f + 0.25f * Mathf.Clamp01(_pen[k].D / 0.05f)));

            // Debug tab tests
            if (now < _testUntil && _testHand == hi && _testAct >= 0) h.Cont[_testAct] = Mathf.Max(h.Cont[_testAct], _testAmp);
            if (now < _rubTestUntil) h.Cont[AGrip] = Mathf.Max(h.Cont[AGrip], TouchAmt * 0.6f);
            if (now < _strokeTestUntil && now >= _strokeTestNext && hi == 1)
            {
                _strokeTestNext = now + 0.45f;
                Pulse(hi, ATrigger, PenAmt * 0.8f, 0.05f, "test stroke", now);
            }
        }

        // Per opening: the deepest finger of YOUR hands (Penetration.Hand >= 0; the partner's animated hands play nothing) -> pulses on
        // going in (6 mm past the opening) / out (4 mm back, or gone) and on each 12 mm turn of the depth while in (at most 5 a second).
        static void PenAll(float now)
        {
            bool pfresh = Penetration.Frame == Time.frameCount;
            for (int k = 0; k < Penetration.NO; k++)
            {
                int hand = Penetration.Hand[k];
                float d = pfresh && Penetration.Ok[k] && hand >= 0 && hand <= 1 ? Penetration.Depth[k] : float.NaN;
                if (!float.IsNaN(d) && !_pen[k].In) _pen[k].Owner = hand;
                if (_pen[k].Owner < 0 || _pen[k].Owner > 1) { _pen[k].In = false; continue; }
                PenStep(_pen[k].Owner, k, d, now);
            }
        }

        static void PenStep(int hi, int k, float d, float now)
        {
            var p = _pen[k];
            string what = Penetration.Names[k];
            if (float.IsNaN(d))
            {
                if (p.In) { p.In = false; Pulse(hi, ATrigger, PenAmt * 0.5f, 0.04f, "out of her " + what, now); }
                _pen[k] = p; return;
            }
            if (!p.In)
            {
                if (d > 0.006f) { p.In = true; p.Dir = 1; p.Ext = d; Pulse(hi, ATrigger, PenAmt * 0.9f, 0.06f, "into her " + what, now); }
            }
            else if (d < -0.004f) { p.In = false; Pulse(hi, ATrigger, PenAmt * 0.5f, 0.04f, "out of her " + what, now); }
            else if (p.Dir > 0)
            {
                if (d > p.Ext) p.Ext = d;
                else if (d < p.Ext - 0.012f) { p.Dir = -1; p.Ext = d; if (now - p.LastT >= 0.2f) { p.LastT = now; Pulse(hi, ATrigger, PenAmt * 0.45f, 0.035f, "out-stroke in her " + what, now); } }
            }
            else
            {
                if (d < p.Ext) p.Ext = d;
                else if (d > p.Ext + 0.012f) { p.Dir = 1; p.Ext = d; if (now - p.LastT >= 0.2f) { p.LastT = now; Pulse(hi, ATrigger, PenAmt * 0.75f, 0.05f, "in-stroke in her " + what, now); } }
            }
            p.D = d;
            _pen[k] = p;
        }

        // The actuators' levels -> impulses on the channels the path has
        static void Output(int hi, Hand h, float now)
        {
            int nch = Localized ? NA : 1;
            for (int ch = 0; ch < nch; ch++)
            {
                float cont = 0f, pulse = 0f, pulseEnd = 0f; bool pnew = false;
                for (int a = 0; a < NA; a++)
                {
                    if (Localized && a != ch) continue;
                    cont = Mathf.Max(cont, h.Cont[a]);
                    if (now < h.PulseUntil[a] && h.PulseAmp[a] > pulse) { pulse = h.PulseAmp[a]; pulseEnd = h.PulseUntil[a]; }
                    if (h.PulseNew[a]) { pnew = true; h.PulseNew[a] = false; }
                }
                float lvl = Mathf.Clamp01(Mathf.Max(cont, pulse) * Strength);
                bool buzz = cont * Strength >= 0.01f;
                if (buzz != h.Buzz[ch])
                {
                    h.Buzz[ch] = buzz;
                    if (Log >= 2) L(HandNames[hi] + " " + (Localized ? ActNames[ch] : "motor") + " buzz " + (buzz ? "on (" + lvl.ToString("0.00") + ", slide " + h.Slide.ToString("0.00") + " m/s)" : "off"));
                }
                if (lvl >= 0.01f)
                {
                    if (pnew || now >= h.NextSend[ch])
                    {
                        float dur = pulse > cont ? Mathf.Max(0.02f, pulseEnd - now) : 0.1f;   // a pulse ends on its own; a buzz is refreshed
                        if (Send(hi, Localized ? ch : AGrip, lvl, dur)) { h.SentUntil[ch] = now + dur; h.SentAmp[ch] = lvl; _nSend++; }
                        h.NextSend[ch] = now + 1f / 30f;
                    }
                }
                else if (h.SentUntil[ch] > now) { StopCh(hi, Localized ? ch : AGrip); h.SentUntil[ch] = 0f; h.SentAmp[ch] = 0f; }
            }
        }

        static void StopAll()
        {
            for (int hi = 0; hi < 2; hi++)
            {
                var h = _h[hi];
                for (int ch = 0; ch < NA; ch++)
                {
                    if (h.SentUntil[ch] > Time.unscaledTime) StopCh(hi, Localized ? ch : AGrip);
                    h.SentUntil[ch] = 0f; h.SentAmp[ch] = 0f; h.Cont[ch] = 0f; h.PulseUntil[ch] = 0f; h.Buzz[ch] = false; h.Was[ch] = false;
                }
            }
            for (int k = 0; k < _pen.Length; k++) _pen[k] = default(Pen);
        }

        // ================= tests (Debug tab) =================

        public static void TestActuator(int hand, int act)
        {
            _testHand = hand; _testAct = act; _testAmp = 0.8f; _testUntil = Time.unscaledTime + 0.5f;
            L("test: " + HandNames[hand] + " " + ActNames[act] + " at 0.80 for 0.5 s via " + (Path.Length > 0 ? Path : "no path") + (Localized || act == AGrip ? "" : " (one motor: the main motor plays it)"));
        }
        public static void TestSlap(int hand) { _testAct = -1; _testUntil = Mathf.Max(_testUntil, Time.unscaledTime + 0.1f); Pulse(hand, AGrip, ImpactAmt, 0.07f, "test slap", Time.unscaledTime); }
        public static void TestRub() { _rubTestUntil = Time.unscaledTime + 1.5f; L("test: rub buzz on both main motors for 1.5 s"); }
        public static void TestStrokes() { _strokeTestUntil = Time.unscaledTime + 2.5f; _strokeTestNext = 0f; L("test: strokes on the right trigger actuator for 2.5 s"); }
        public static bool Testing { get { float n = Time.unscaledTime; return n < _testUntil || n < _rubTestUntil || n < _strokeTestUntil; } }

        // ================= the path =================

        static int _kind;                    // 0 none, 1 OpenXR localized, 2 OpenXR one motor, 3 XR device
        static string _why = "";
        static float _nextProbe = 3f;        // a few seconds after start: the XR devices are there
        static string _logged = "";
        static bool _reflected, _oxOk, _xrOk;
        // Input System / OpenXR
        static ConstructorInfo _actCtor; static object _passThrough; static MethodInfo _actEnable, _actDisable;
        static PropertyInfo _actControls;
        static MethodInfo _oxSend, _oxStop, _oxHandle, _rtExt; static PropertyInfo _rtName, _rtVer;
        static readonly object[,] _actLoc = new object[2, NA];
        static readonly object[] _actOne = new object[2];
        static readonly object[] _sendArgs = new object[5], _stopArgs = new object[2];
        // XR InputDevice
        static MethodInfo _xrGetAt, _xrImpulse, _xrStop, _xrCaps; static Type _xrNode;
        static readonly object[] _xrDev = new object[2];
        static readonly object[] _impArgs = new object[3];

        static Type FindType(string full, string asmStart)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asmStart != null && !a.GetName().Name.StartsWith(asmStart, StringComparison.Ordinal)) continue;
                var t = a.GetType(full, false);
                if (t != null) return t;
            }
            return null;
        }

        static void Reflect()
        {
            _reflected = true;
            try
            {
                var act = FindType("UnityEngine.InputSystem.InputAction", "Unity.InputSystem");
                var actType = FindType("UnityEngine.InputSystem.InputActionType", "Unity.InputSystem");
                var dev = FindType("UnityEngine.InputSystem.InputDevice", "Unity.InputSystem");
                var ox = FindType("UnityEngine.XR.OpenXR.Input.OpenXRInput", "Unity.XR.OpenXR");
                var rt = FindType("UnityEngine.XR.OpenXR.OpenXRRuntime", "Unity.XR.OpenXR");
                if (act != null && actType != null && dev != null && ox != null)
                {
                    foreach (var c in act.GetConstructors())
                    {
                        var ps = c.GetParameters();
                        if (ps.Length == 6 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == actType) { _actCtor = c; break; }
                    }
                    _passThrough = Enum.Parse(actType, "PassThrough");
                    _actEnable = act.GetMethod("Enable", Type.EmptyTypes);
                    _actDisable = act.GetMethod("Disable", Type.EmptyTypes);
                    _actControls = act.GetProperty("controls");
                    foreach (var m in ox.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        var ps = m.GetParameters();
                        if (m.Name == "SendHapticImpulse" && ps.Length == 5 && ps[0].ParameterType == act && ps[4].ParameterType == dev) _oxSend = m;
                        else if (m.Name == "StopHaptics" && ps.Length == 2 && ps[0].ParameterType == act && ps[1].ParameterType == dev) _oxStop = m;
                        else if (m.Name == "GetActionHandle" && ps.Length == 2 && ps[0].ParameterType == act) _oxHandle = m;
                    }
                    _oxOk = _actCtor != null && _actEnable != null && _actControls != null && _oxSend != null;
                }
                if (rt != null)
                {
                    _rtName = rt.GetProperty("name", BindingFlags.Public | BindingFlags.Static);
                    _rtVer = rt.GetProperty("version", BindingFlags.Public | BindingFlags.Static);
                    _rtExt = rt.GetMethod("IsExtensionEnabled", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                }
            }
            catch (Exception e) { _oxOk = false; L("OpenXR / Input System reflection failed: " + e.Message); }
            try
            {
                var di = FindType("UnityEngine.XR.InputDevices", "UnityEngine");
                _xrNode = FindType("UnityEngine.XR.XRNode", "UnityEngine");
                var dev = FindType("UnityEngine.XR.InputDevice", "UnityEngine");
                if (di != null && _xrNode != null && dev != null)
                {
                    _xrGetAt = di.GetMethod("GetDeviceAtXRNode", BindingFlags.Public | BindingFlags.Static);
                    _xrImpulse = dev.GetMethod("SendHapticImpulse", new[] { typeof(uint), typeof(float), typeof(float) });
                    _xrStop = dev.GetMethod("StopHaptics", Type.EmptyTypes);
                    foreach (var m in dev.GetMethods()) if (m.Name == "TryGetHapticCapabilities" && m.GetParameters().Length == 1) _xrCaps = m;
                    _xrOk = _xrGetAt != null && _xrImpulse != null;
                }
            }
            catch (Exception e) { _xrOk = false; L("XR InputDevice reflection failed: " + e.Message); }
        }

        static object NewAction(string name, string binding)
        {
            var a = _actCtor.Invoke(new object[] { name, _passThrough, binding, null, null, null });
            _actEnable.Invoke(a, null);
            return a;
        }

        static void DropActions()
        {
            for (int h = 0; h < 2; h++)
            {
                for (int k = 0; k < NA; k++) { if (_actLoc[h, k] != null) try { _actDisable.Invoke(_actLoc[h, k], null); } catch { } _actLoc[h, k] = null; }
                if (_actOne[h] != null) try { _actDisable.Invoke(_actOne[h], null); } catch { }
                _actOne[h] = null;
            }
        }

        // How many controls the action bound, and the first one's device (for the log)
        static int Bound(object action, out string dev)
        {
            dev = "";
            if (action == null) return 0;
            try
            {
                var ctrls = _actControls.GetValue(action, null);
                int n = (int)ctrls.GetType().GetProperty("Count").GetValue(ctrls, null);
                if (n > 0)
                {
                    var c0 = ctrls.GetType().GetProperty("Item").GetValue(ctrls, new object[] { 0 });
                    var d = c0.GetType().GetProperty("device").GetValue(c0, null);
                    dev = d != null ? Convert.ToString(d.GetType().GetProperty("layout").GetValue(d, null)) : "";
                }
                if (n > 0 && _oxHandle != null)
                {
                    ulong hnd = Convert.ToUInt64(_oxHandle.Invoke(null, new object[] { action, null }));
                    if (hnd == 0UL) { dev += " (no OpenXR action handle)"; return 0; }
                }
                return n;
            }
            catch (Exception) { return 0; }
        }

        static void Probe(float now)
        {
            if (now < _nextProbe) return;
            _nextProbe = now + (_kind == 0 ? 2f : 5f);
            if (!_reflected) Reflect();
            int kind = 0; string path = "", why = "";
            string rtName = "", ext = "";
            try
            {
                if (_rtName != null) rtName = Convert.ToString(_rtName.GetValue(null, null)) + " " + Convert.ToString(_rtVer != null ? _rtVer.GetValue(null, null) : "");
                if (_rtExt != null) ext = (bool)_rtExt.Invoke(null, new object[] { "XR_FB_touch_controller_pro" }) ? "on" : "off";
            }
            catch (Exception) { }
            rtName = rtName.Trim();

            if (PathPick == 0 && _oxOk)
            {
                try
                {
                    // localized: only when the runtime enabled the Touch Pro extension (VDXR / SteamVR do not)
                    if (ext == "on")
                    {
                        string[] ctl = { "haptic", "hapticTrigger", "hapticThumb" };
                        int ok = 0; string devs = "";
                        for (int h = 0; h < 2; h++)
                            for (int k = 0; k < NA; k++)
                            {
                                if (_actLoc[h, k] == null) _actLoc[h, k] = NewAction("sfm_haptic_" + h + k, "<QuestProTouchController>{" + (h == 0 ? "LeftHand" : "RightHand") + "}/" + ctl[k]);
                                string d; if (Bound(_actLoc[h, k], out d) > 0) { ok++; if (devs.Length == 0) devs = d; }
                            }
                        if (ok == 2 * NA) { kind = 1; path = "OpenXR localized (grip, trigger, thumb actuators; " + devs + ")"; }
                        else if (ok > 0) why = "Touch Pro profile on, " + ok + "/6 actuator bindings found";
                        else why = "Touch Pro extension on but no QuestProTouchController device";
                    }
                    if (kind == 0)
                    {
                        int ok = 0; string devs = "";
                        for (int h = 0; h < 2; h++)
                        {
                            if (_actOne[h] == null) _actOne[h] = NewAction("sfm_haptic_" + h, "<XRController>{" + (h == 0 ? "LeftHand" : "RightHand") + "}/haptic");
                            string d; if (Bound(_actOne[h], out d) > 0) { ok++; devs += (devs.Length > 0 ? " / " : "") + d; }
                        }
                        if (ok > 0) { kind = 2; path = "OpenXR one motor per controller (" + devs + ")"; }
                        else if (why.Length == 0) why = "no OpenXR controller with a haptic output bound yet";
                    }
                }
                catch (Exception e) { why = "OpenXR actions failed: " + e.Message; }
            }
            if (kind == 0 && _xrOk)
            {
                int ok = 0; string caps = "";
                for (int h = 0; h < 2; h++)
                {
                    try
                    {
                        var dev = _xrGetAt.Invoke(null, new object[] { Enum.ToObject(_xrNode, h == 0 ? 4 : 5) });
                        var valid = dev.GetType().GetProperty("isValid").GetValue(dev, null);
                        if (!(valid is bool) || !(bool)valid) { _xrDev[h] = null; continue; }
                        _xrDev[h] = dev; ok++;
                        if (h != 0) continue;
                        caps = Convert.ToString(dev.GetType().GetProperty("name").GetValue(dev, null));
                        var args = new object[1];
                        if (_xrCaps != null && (bool)_xrCaps.Invoke(dev, args) && args[0] != null)
                        {
                            var c = args[0]; var ct = c.GetType();   // HapticCapabilities: a struct with properties
                            caps += ", " + ct.GetProperty("numChannels").GetValue(c, null) + " channel(s), impulse " + ct.GetProperty("supportsImpulse").GetValue(c, null);
                        }
                    }
                    catch (Exception) { _xrDev[h] = null; }
                }
                if (ok > 0) { kind = 3; path = "XR InputDevice channel 0 (" + caps + ")"; }
                else if (why.Length == 0) why = "no XR controller yet";
            }
            else if (kind == 0 && why.Length == 0) why = "no OpenXR or XR input API found";

            if (kind != _kind || path != Path) { if (_kind != 0 && kind != _kind) StopAll(); _kind = kind; Path = path; }
            _why = why;
            string line = kind != 0
                ? "runtime " + (rtName.Length > 0 ? rtName : "?") + ", XR_FB_touch_controller_pro " + (ext.Length > 0 ? ext : "?") + " -> " + path
                  + (kind == 1 ? "" : kind == 2 && ext == "off" ? " (the runtime offers no Touch Pro profile: trigger / thumb touches play on the main motor)" : "")
                  + (PathPick == 1 ? " (Path = XR device only)" : "")
                : "runtime " + (rtName.Length > 0 ? rtName : "?") + ": no path yet (" + why + ")";
            if (line != _logged && (kind != 0 || _logged.Length == 0)) { _logged = line; L(line); }
        }

        static bool Send(int hi, int act, float amp, float dur)
        {
            try
            {
                switch (_kind)
                {
                    case 1:
                    case 2:
                        {
                            var a = _kind == 1 ? _actLoc[hi, act] : _actOne[hi];
                            if (a == null) return false;
                            _sendArgs[0] = a; _sendArgs[1] = amp; _sendArgs[2] = 0f; _sendArgs[3] = dur; _sendArgs[4] = null;   // frequency 0: the runtime's own
                            _oxSend.Invoke(null, _sendArgs);
                            return true;
                        }
                    case 3:
                        {
                            var d = _xrDev[hi];
                            if (d == null) return false;
                            _impArgs[0] = 0u; _impArgs[1] = amp; _impArgs[2] = dur;
                            return (bool)_xrImpulse.Invoke(d, _impArgs);
                        }
                }
            }
            catch (Exception e)
            {
                L("sending failed (" + e.GetType().Name + ": " + (e.InnerException != null ? e.InnerException.Message : e.Message) + "), looking for another path");
                _kind = 0; Path = ""; _nextProbe = Time.unscaledTime + 2f;
            }
            return false;
        }

        static void StopCh(int hi, int act)
        {
            try
            {
                if (_kind == 1 || _kind == 2)
                {
                    var a = _kind == 1 ? _actLoc[hi, act] : _actOne[hi];
                    if (a == null) return;
                    if (_oxStop != null) { _stopArgs[0] = a; _stopArgs[1] = null; _oxStop.Invoke(null, _stopArgs); }
                    else { _sendArgs[0] = a; _sendArgs[1] = 0f; _sendArgs[2] = 0f; _sendArgs[3] = 0.02f; _sendArgs[4] = null; _oxSend.Invoke(null, _sendArgs); }
                }
                else if (_kind == 3 && _xrDev[hi] != null)
                {
                    if (_xrStop != null) _xrStop.Invoke(_xrDev[hi], null);
                    else { _impArgs[0] = 0u; _impArgs[1] = 0f; _impArgs[2] = 0.01f; _xrImpulse.Invoke(_xrDev[hi], _impArgs); }
                }
            }
            catch (Exception) { }
        }

        public static void Release()
        {
            StopAll();
            try { DropActions(); } catch { }
            _kind = 0; Path = ""; _logged = ""; _nextProbe = Time.unscaledTime + 2f;
        }
    }
}
