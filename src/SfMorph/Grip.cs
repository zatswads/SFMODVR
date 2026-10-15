using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // The VR controllers' grip and trigger (0..1), read once per frame in the apply step and shared by the physics hands and Embody.
    // Source per hand and per channel, chosen when first needed, logged, retried every 2 s while none works:
    //   grip:    (1) VR_HandController.selectValue_left/right (or a numeric field named grip*), (2) the controller Animator's float
    //            parameter containing grip/select/trigger, (3) UnityEngine.XR.InputDevices grip through reflection.
    //   trigger: (1) VR_HandController.activateValue_left/right (or a numeric field named trigger*; never a select/grip field),
    //            (2) the controller Animator's float parameter containing trigger, (3) UnityEngine.XR.InputDevices trigger.
    // Update() is idempotent within a frame (guarded by Time.frameCount), so several callers can ask for it.
    internal static class Grip
    {
        public static float Left, Right;
        // 0.8.0: the trigger (VR_HandController.activateValue_*), 0..1, read in the same Update as Left/Right. Index finger.
        public static float TriggerLeft, TriggerRight;
        // 0.8.0: what closes the hand (all five fingers together, no pointing pose): the button picked by [Hands] GrabButton
        // (0 trigger, the default: the grip is the game's menu button on some controllers; 1 grip; 2 whichever is pressed further)
        public static int GrabButton;
        public static float GrabLeft, GrabRight;
        public static string Source = "";

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // one analog channel of one hand
        sealed class Chan
        {
            public int Kind;                      // 0 none, 1 VR_HandController field, 2 controller Animator parameter, 3 XR
            public string Desc = "none";
            public float NextTry, Last;
            public Component Owner; public FieldInfo Field; public Func<object, float> Getter;
            public Animator Anim; public int Hash;
            public object XrDev; public readonly object[] XrArgs = new object[2]; public int XrFail;
            public bool Warned;
        }

        sealed class Side
        {
            public bool Left;
            public readonly Chan G = new Chan(), T = new Chan();
        }

        static readonly Side _l = new Side { Left = true }, _r = new Side { Left = false };
        static int _frame = -1;
        static readonly HashSet<string> _once = new HashSet<string>();

        static void LogOnce(string key, string msg) { if (_once.Add(key)) Plugin.Log.LogInfo("Grip: " + msg); }

        public static void Update()
        {
            int f = Time.frameCount;
            if (f == _frame) return;
            _frame = f;
            try
            {
                float now = Time.unscaledTime;
                Left = Read(_l, _l.G, false, now);
                Right = Read(_r, _r.G, false, now);
                TriggerLeft = Read(_l, _l.T, true, now);
                TriggerRight = Read(_r, _r.T, true, now);
                GrabLeft = Grab(Left, TriggerLeft);
                GrabRight = Grab(Right, TriggerRight);
            }
            catch (Exception e)
            {
                Left = Right = TriggerLeft = TriggerRight = GrabLeft = GrabRight = 0f;
                LogOnce("err" + e.GetType().Name, "update error: " + e.Message);
            }
        }

        static float Grab(float grip, float trigger)
        {
            float g = float.IsNaN(grip) ? 0f : Mathf.Clamp01(grip), t = float.IsNaN(trigger) ? 0f : Mathf.Clamp01(trigger);
            return GrabButton == 1 ? g : GrabButton == 2 ? Mathf.Max(g, t) : t;
        }

        static void SetSource()
        {
            Source = "L: grip " + _l.G.Desc + ", trigger " + _l.T.Desc + "; R: grip " + _r.G.Desc + ", trigger " + _r.T.Desc;
        }

        static Func<object, float> MakeGetter(FieldInfo f)
        {
            var t = f.FieldType;
            if (t != typeof(float) && t != typeof(double) && t != typeof(int) && t != typeof(bool)) return null;
            try
            {
                // a compiled getter, so reading the grip every frame doesn't box
                var dm = new DynamicMethod("sfm_grip_" + f.Name, typeof(float), new[] { typeof(object) }, typeof(Grip).Module, true);
                var il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, f.DeclaringType);
                il.Emit(OpCodes.Ldfld, f);
                if (t != typeof(float)) il.Emit(OpCodes.Conv_R4);
                il.Emit(OpCodes.Ret);
                return (Func<object, float>)dm.CreateDelegate(typeof(Func<object, float>));
            }
            catch (Exception) { return null; }
        }

        static bool IsNumberType(Type t) { return t == typeof(float) || t == typeof(double) || t == typeof(int) || t == typeof(bool); }

        static bool SideName(string n, bool left)
        {
            n = n.ToLowerInvariant();
            return left ? n.Contains("left") || n.EndsWith("_l", StringComparison.Ordinal) : n.Contains("right") || n.EndsWith("_r", StringComparison.Ordinal);
        }

        // (Re)finds the source of one channel: a VR_HandController field, else an Animator parameter, else XR.
        static void Resolve(Side h, Chan c, bool trig)
        {
            c.Kind = 0; c.Desc = "none"; c.Field = null; c.Getter = null; c.Anim = null; c.XrDev = null; c.Owner = null;
            string what = trig ? "trigger" : "grip";
            var hc = PhysicsHands.HandController();
            var handType = PhysicsHands.HandType;
            if (hc != null && handType != null)
            {
                FieldInfo best = null; int bestScore = 0;
                var cand = new StringBuilder();
                foreach (var f in handType.GetFields(Any))
                {
                    if (f.IsStatic || !IsNumberType(f.FieldType)) continue;
                    var ln = f.Name.ToLowerInvariant();
                    if (!trig && (ln.Contains("select") || ln.Contains("grip") || ln.Contains("trigger") || ln.Contains("activate"))) cand.Append(cand.Length > 0 ? ", " : "").Append(f.Name);
                    if (!SideName(f.Name, h.Left)) continue;
                    int score;
                    if (trig) score = ln.Contains("select") || ln.Contains("grip") ? 0 : ln.Contains("activatevalue") ? 3 : ln.Contains("trigger") ? 2 : 0;
                    else score = ln.Contains("selectvalue") ? 3 : ln.Contains("grip") ? 2 : 0;
                    if (score > bestScore) { best = f; bestScore = score; }
                }
                if (!trig && !c.Warned && cand.Length > 0) LogOnce("gripfields", "VR_HandController numeric fields about select/grip/trigger/activate: " + cand);
                if (best != null)
                {
                    c.Kind = 1; c.Owner = hc; c.Field = best; c.Getter = MakeGetter(best);
                    c.Desc = "VR_HandController." + best.Name;
                }
                else
                {
                    var anim = PhysicsHands.Get(hc, h.Left ? "controller_left" : "controller_right") as Animator;
                    if (anim != null)
                    {
                        string pick = null; int sc = 0;
                        try
                        {
                            foreach (var p in anim.parameters)
                            {
                                if (p.type != AnimatorControllerParameterType.Float) continue;
                                var ln = p.name.ToLowerInvariant();
                                int score;
                                if (trig) score = ln.Contains("trigger") ? 3 : 0;
                                else score = ln.Contains("grip") ? 3 : ln.Contains("select") ? 2 : ln.Contains("trigger") ? 1 : 0;
                                if (score > sc) { sc = score; pick = p.name; }
                            }
                        }
                        catch (Exception) { }
                        if (pick != null)
                        {
                            c.Kind = 2; c.Anim = anim; c.Hash = Animator.StringToHash(pick);
                            c.Desc = "controller Animator parameter '" + pick + "'";
                        }
                    }
                }
            }
            if (c.Kind == 0 && XrInit())
            {
                try
                {
                    var dev = _xrGetAt.Invoke(null, new object[] { Enum.ToObject(_xrNode, h.Left ? 4 : 5) });
                    var valid = dev.GetType().GetProperty("isValid").GetValue(dev, null);
                    if (valid is bool && (bool)valid)
                    {
                        c.XrDev = dev; c.XrFail = 0;
                        c.Kind = 3; c.Desc = "XR InputDevices " + what;
                    }
                }
                catch (Exception) { }
            }
            if (c.Kind != 0) Plugin.Log.LogInfo("Grip: " + (h.Left ? "left" : "right") + " " + what + " from " + c.Desc);
            else if (!c.Warned) { c.Warned = true; Plugin.Log.LogInfo("Grip: " + (h.Left ? "left" : "right") + " " + what + ": no source found yet (value stays 0, retrying every 2 s)"); }
            SetSource();
        }

        static int _xrState;   // 0 not looked, 1 usable, 2 not
        static Type _xrNode;
        static MethodInfo _xrGetAt, _xrTry;
        static object _xrUsage, _xrUsageT;

        static bool XrInit()
        {
            if (_xrState != 0) return _xrState == 1;
            _xrState = 2;
            try
            {
                Type di = null, node = null, cu = null, dev = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!a.GetName().Name.StartsWith("UnityEngine", StringComparison.Ordinal)) continue;
                    di = di ?? a.GetType("UnityEngine.XR.InputDevices");
                    node = node ?? a.GetType("UnityEngine.XR.XRNode");
                    cu = cu ?? a.GetType("UnityEngine.XR.CommonUsages");
                    dev = dev ?? a.GetType("UnityEngine.XR.InputDevice");
                }
                if (di == null || node == null || cu == null || dev == null) return false;
                _xrGetAt = di.GetMethod("GetDeviceAtXRNode", BindingFlags.Public | BindingFlags.Static);
                var grip = cu.GetField("grip", BindingFlags.Public | BindingFlags.Static);
                var trigger = cu.GetField("trigger", BindingFlags.Public | BindingFlags.Static);
                if (_xrGetAt == null || grip == null) return false;
                _xrUsage = grip.GetValue(null);
                _xrUsageT = trigger != null && trigger.FieldType == grip.FieldType ? trigger.GetValue(null) : null;   // both InputFeatureUsage<float>
                foreach (var m in dev.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "TryGetFeatureValue") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == grip.FieldType && ps[1].ParameterType == typeof(float).MakeByRefType()) { _xrTry = m; break; }
                }
                if (_xrTry == null) return false;
                _xrNode = node;
                _xrState = 1;
            }
            catch (Exception) { _xrState = 2; }
            return _xrState == 1;
        }

        static float Read(Side h, Chan c, bool trig, float now)
        {
            switch (c.Kind)
            {
                case 1:
                    if (c.Owner == null) { c.Kind = 0; break; }
                    try { return c.Last = Mathf.Clamp01(c.Getter != null ? c.Getter(c.Owner) : Convert.ToSingle(c.Field.GetValue(c.Owner))); }
                    catch (Exception) { c.Kind = 0; break; }
                case 2:
                    if (c.Anim == null) { c.Kind = 0; break; }
                    return c.Last = Mathf.Clamp01(c.Anim.GetFloat(c.Hash));
                case 3:
                    try
                    {
                        object usage = trig ? _xrUsageT : _xrUsage;
                        if (usage == null) { c.Kind = 0; break; }
                        c.XrArgs[0] = usage; c.XrArgs[1] = 0f;   // a few small boxes per call: only on this last-resort source
                        if ((bool)_xrTry.Invoke(c.XrDev, c.XrArgs)) { c.XrFail = 0; return c.Last = Mathf.Clamp01((float)c.XrArgs[1]); }
                        if (++c.XrFail > 120) c.Kind = 0;
                        return c.Last;
                    }
                    catch (Exception) { c.Kind = 0; break; }
            }
            c.Last = 0f;
            if (now >= c.NextTry) { c.NextTry = now + 2f; Resolve(h, c, trig); }
            return 0f;
        }
    }
}
