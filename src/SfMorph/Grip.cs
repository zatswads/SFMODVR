using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // The VR controllers' grip (0..1), read once per frame in the apply step and shared by the physics hands and Embody.
    // Source per hand, chosen when first needed, logged, retried every 2 s while none works:
    //   (1) VR_HandController.selectValue_left/right (or a numeric field named grip*), (2) the controller Animator's float
    //   parameter containing grip/select/trigger, (3) UnityEngine.XR.InputDevices grip through reflection.
    // Update() is idempotent within a frame (guarded by Time.frameCount), so several callers can ask for it.
    internal static class Grip
    {
        public static float Left, Right;
        public static string Source = "";

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        sealed class Side
        {
            public bool Left;
            public int Kind;                      // 0 none, 1 VR_HandController field, 2 controller Animator parameter, 3 XR
            public string Desc = "none";
            public float NextTry, Last;
            public Component Owner; public FieldInfo Field; public Func<object, float> Getter;
            public Animator Anim; public int Hash;
            public object XrDev; public readonly object[] XrArgs = new object[2]; public int XrFail;
            public bool Warned;
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
                Left = Read(_l, now);
                Right = Read(_r, now);
            }
            catch (Exception e)
            {
                Left = Right = 0f;
                LogOnce("err" + e.GetType().Name, "update error: " + e.Message);
            }
        }

        static void SetSource()
        {
            Source = "L: " + _l.Desc + ", R: " + _r.Desc;
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

        static void Resolve(Side h)
        {
            h.Kind = 0; h.Desc = "none"; h.Field = null; h.Getter = null; h.Anim = null; h.XrDev = null; h.Owner = null;
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
                    if (ln.Contains("select") || ln.Contains("grip") || ln.Contains("trigger")) cand.Append(cand.Length > 0 ? ", " : "").Append(f.Name);
                    if (!SideName(f.Name, h.Left)) continue;
                    int score = ln.Contains("selectvalue") ? 3 : ln.Contains("grip") ? 2 : 0;
                    if (score > bestScore) { best = f; bestScore = score; }
                }
                if (!h.Warned && cand.Length > 0) LogOnce("gripfields", "VR_HandController numeric fields about select/grip/trigger: " + cand);
                if (best != null)
                {
                    h.Kind = 1; h.Owner = hc; h.Field = best; h.Getter = MakeGetter(best);
                    h.Desc = "VR_HandController." + best.Name;
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
                                int score = ln.Contains("grip") ? 3 : ln.Contains("select") ? 2 : ln.Contains("trigger") ? 1 : 0;
                                if (score > sc) { sc = score; pick = p.name; }
                            }
                        }
                        catch (Exception) { }
                        if (pick != null)
                        {
                            h.Kind = 2; h.Anim = anim; h.Hash = Animator.StringToHash(pick);
                            h.Desc = "controller Animator parameter '" + pick + "'";
                        }
                    }
                }
            }
            if (h.Kind == 0 && XrInit())
            {
                try
                {
                    var dev = _xrGetAt.Invoke(null, new object[] { Enum.ToObject(_xrNode, h.Left ? 4 : 5) });
                    var valid = dev.GetType().GetProperty("isValid").GetValue(dev, null);
                    if (valid is bool && (bool)valid)
                    {
                        h.XrDev = dev; h.XrFail = 0;
                        h.Kind = 3; h.Desc = "XR InputDevices grip";
                    }
                }
                catch (Exception) { }
            }
            if (h.Kind != 0) Plugin.Log.LogInfo("Grip: " + (h.Left ? "left" : "right") + " grip from " + h.Desc);
            else if (!h.Warned) { h.Warned = true; Plugin.Log.LogInfo("Grip: " + (h.Left ? "left" : "right") + " grip: no source found yet (value stays 0, retrying every 2 s)"); }
            SetSource();
        }

        static int _xrState;   // 0 not looked, 1 usable, 2 not
        static Type _xrNode;
        static MethodInfo _xrGetAt, _xrTry;
        static object _xrUsage;

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
                if (_xrGetAt == null || grip == null) return false;
                _xrUsage = grip.GetValue(null);
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

        static float Read(Side h, float now)
        {
            switch (h.Kind)
            {
                case 1:
                    if (h.Owner == null) { h.Kind = 0; break; }
                    try { return h.Last = Mathf.Clamp01(h.Getter != null ? h.Getter(h.Owner) : Convert.ToSingle(h.Field.GetValue(h.Owner))); }
                    catch (Exception) { h.Kind = 0; break; }
                case 2:
                    if (h.Anim == null) { h.Kind = 0; break; }
                    return h.Last = Mathf.Clamp01(h.Anim.GetFloat(h.Hash));
                case 3:
                    try
                    {
                        h.XrArgs[0] = _xrUsage; h.XrArgs[1] = 0f;   // a few small boxes per call: only on this last-resort source
                        if ((bool)_xrTry.Invoke(h.XrDev, h.XrArgs)) { h.XrFail = 0; return h.Last = Mathf.Clamp01((float)h.XrArgs[1]); }
                        if (++h.XrFail > 120) h.Kind = 0;
                        return h.Last;
                    }
                    catch (Exception) { h.Kind = 0; break; }
            }
            h.Last = 0f;
            if (now >= h.NextTry) { h.NextTry = now + 2f; Resolve(h); }
            return 0f;
        }
    }
}
