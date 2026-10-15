using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SfMorph
{
    // 0.8.0 ripple shockwave (design 4): impacts (SoftSim's fresh fast contacts via SoftZones.Impacts, Reactions' slaps via Impact) become
    // <= 8 expanding damped rings that GpuContact's Compose kernel adds to her soft skin, plus a short pulse on the breast DynamicBones.
    // Everything runs on the main thread: Update after Simulate, Fill from GpuContact.Run (a render-loop step on the main thread too).
    internal static class Ripple
    {
        public const int MaxWaves = 8;
        public static string Status = "";

        struct Wave
        {
            public Transform Bone; public bool HasBone;   // HasBone && Bone == null: the bone is gone, the wave dies
            public Vector3 Local;                         // in the bone's space (world when there is no bone)
            public float T0, A, C, Lambda, Tau;
            public bool Live;
        }

        struct Pending { public Vector3 P, Dir; public float Speed; public Transform Bone; public string By; }

        // the game's breast DynamicBone m_Force, borrowed for 0.12 s: put back only if the field still holds what we wrote
        sealed class Pulse { public Component Db; public Vector3 Base, Ours; public float Until; public bool Active; }

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const float PulseSeconds = 0.12f, PulseForce = 0.006f;

        static readonly Wave[] _w = new Wave[MaxWaves];
        static int _next, _count;
        static readonly Pending[] _pend = new Pending[4];
        static int _pendN;
        static ContactSettings _s;
        static int _frame = -1;

        // logs and status
        static int _impacts, _sumImpacts;
        static float _sumMax, _nextSummary, _lastSpeed, _lastAt = -100f;
        static string _lastBy = "", _lastWhere = "";
        static float _statusAt;

        // breast pulse
        static readonly List<Component> _dbs = new List<Component>();
        static readonly List<Pulse> _pulses = new List<Pulse>();
        static Type _fType;
        static FieldInfo _fForce;
        static bool _fWarned;

        // Live waves right now.
        public static int Count { get { return _count; } }

        // Main thread, every frame after Simulate (Body.ApplyAll): reads z.Impacts, ages the waves, services the breast pulses.
        public static void Update(Character her, SoftZones z, ContactSettings s, float now)
        {
            if (s == null) return;
            _s = s;
            int fr = Time.frameCount;
            bool newFrame = fr != _frame;   // Simulate's impacts are read once per frame
            _frame = fr;
            if (newFrame && z != null)
            {
                int n = Mathf.Min(z.ImpactCount, z.Impacts.Length);
                for (int i = 0; i < n; i++)
                {
                    var im = z.Impacts[i];
                    AddImpact(im.P, im.Dir, im.Speed, im.Bone, im.By, SoftZones.ZoneId(im.Zone), now);
                }
            }
            for (int i = 0; i < _pendN; i++)
            {
                var p = _pend[i];
                AddImpact(p.P, p.Dir, p.Speed, p.Bone, p.By, "", now);
            }
            _pendN = 0;
            Age(now);
            ServicePulses(now);
            if (now >= _statusAt) { _statusAt = now + 0.25f; UpdateStatus(s, now); }
            if (now >= _nextSummary)
            {
                _nextSummary = now + 10f;
                if (_sumImpacts > 0 || _count > 0)
                    Plugin.Log.LogInfo("Ripple: last 10 s: " + _sumImpacts + " impacts (fastest " + _sumMax.ToString("0.0") + " m/s), " + _count + " waves live");
                _sumImpacts = 0; _sumMax = 0f;
            }
        }

        // An impact from outside the soft zones (Reactions: a slap). speed m/s, dir = into her (world), bone = the wave follows it.
        public static void Impact(Vector3 point, Vector3 dir, float speed, Transform bone, string by)
        {
            if (_s == null) return;   // not running (physics off / released): a slap now must not become a wave when it comes back
            if (!Finite(point) || !(speed > 0f)) return;
            if (!Finite(dir) || dir.sqrMagnitude < 1e-8f) dir = Vector3.forward;
            if (_pendN == _pend.Length)
            {
                for (int i = 1; i < _pend.Length; i++) _pend[i - 1] = _pend[i];   // drop the oldest
                _pendN--;
            }
            _pend[_pendN++] = new Pending { P = point, Dir = dir.normalized, Speed = speed, Bone = bone, By = by };
        }

        // GPU upload (GpuContact.Run): per live wave two Vector4s: [2i] = (origin world xyz, age s), [2i+1] = (A m, c m/s, lambda m, tau s).
        // arr.Length >= 2 * MaxWaves. Returns the wave count.
        public static int Fill(float now, Vector4[] arr)
        {
            int n = 0;
            if (arr == null || arr.Length < 2 * MaxWaves) return 0;
            for (int i = 0; i < MaxWaves; i++)
            {
                if (!_w[i].Live) continue;
                float age = Mathf.Max(0f, now - _w[i].T0);
                if (Dead(ref _w[i], age)) { _w[i].Live = false; continue; }
                Vector3 o = _w[i].HasBone ? _w[i].Bone.TransformPoint(_w[i].Local) : _w[i].Local;
                arr[2 * n] = new Vector4(o.x, o.y, o.z, age);
                arr[2 * n + 1] = new Vector4(_w[i].A, _w[i].C, _w[i].Lambda, _w[i].Tau);
                n++;
            }
            _count = n;
            return n;
        }

        public static void Release()
        {
            for (int i = 0; i < MaxWaves; i++) _w[i].Live = false;
            _count = 0; _pendN = 0; _next = 0;
            RestorePulses();
            _s = null;
            Status = "";
            _impacts = 0; _sumImpacts = 0; _sumMax = 0f; _nextSummary = 0f; _lastAt = -100f;
        }

        // ---- waves ----

        static bool Dead(ref Wave w, float age)
        {
            if (w.HasBone && w.Bone == null) return true;
            return w.A * Mathf.Exp(-age / Mathf.Max(0.01f, w.Tau)) < 1e-4f;
        }

        static void Age(float now)
        {
            int n = 0;
            for (int i = 0; i < MaxWaves; i++)
            {
                if (!_w[i].Live) continue;
                if (Dead(ref _w[i], Mathf.Max(0f, now - _w[i].T0))) { _w[i].Live = false; continue; }
                n++;
            }
            _count = n;
        }

        static void AddImpact(Vector3 p, Vector3 dir, float speed, Transform bone, string by, string zoneId, float now)
        {
            var s = _s;
            if (s == null || !Finite(p) || !(speed > 0f)) return;
            _impacts++; _sumImpacts++;
            if (speed > _sumMax) _sumMax = speed;
            string bn = bone != null ? bone.name : zoneId;
            _lastSpeed = speed; _lastAt = now; _lastBy = string.IsNullOrEmpty(by) ? "a hand" : by; _lastWhere = bn ?? "";
            bool logIt = _impacts <= 10 || _impacts % 10 == 0;
            if (s.Ripple <= 0f)
            {
                if (logIt) Plugin.Log.LogInfo("Ripple: impact #" + _impacts + " by " + _lastBy + " on " + _lastWhere + " " + speed.ToString("0.0") + " m/s (ripple strength 0: no wave)");
                return;
            }
            float a = Mathf.Clamp(0.004f * (speed - 0.5f), 0f, 0.010f) * s.Ripple;
            // breast impact: the game's breast physics gets a short kick along the impact
            string pulseNote = "";
            if (IsBreast(bone, zoneId))
            {
                bool left = IsLeft(bone, zoneId), right = IsRight(bone, zoneId);
                if (left != right) pulseNote = PulseBreasts(left, dir, Mathf.Clamp01(speed / 3f), now) + " breast bones kicked";
            }
            bool merged = false;
            if (a > 0f)
            {
                for (int i = 0; i < MaxWaves; i++)
                {
                    if (!_w[i].Live) continue;
                    float age = now - _w[i].T0;
                    if (age > 0.15f || age < -0.01f) continue;
                    if (_w[i].HasBone && _w[i].Bone == null) continue;
                    Vector3 o = _w[i].HasBone ? _w[i].Bone.TransformPoint(_w[i].Local) : _w[i].Local;
                    if ((o - p).sqrMagnitude > 0.06f * 0.06f) continue;
                    if (a > _w[i].A) _w[i].A = a;
                    merged = true;
                    break;
                }
                if (!merged)
                {
                    int k = _next;
                    _next = (_next + 1) % MaxWaves;
                    _w[k] = new Wave
                    {
                        Bone = bone, HasBone = bone != null, Local = bone != null ? bone.InverseTransformPoint(p) : p,
                        T0 = now, A = a, C = Mathf.Max(0.05f, s.RippleSpeed), Lambda = Mathf.Max(0.01f, s.RippleWavelength), Tau = Mathf.Max(0.02f, s.RippleDamping), Live = true
                    };
                }
            }
            Age(now);
            if (logIt)
                Plugin.Log.LogInfo("Ripple: impact #" + _impacts + " by " + _lastBy + " on " + _lastWhere + " " + speed.ToString("0.0") + " m/s -> "
                    + (a <= 0f ? "too slow for a wave" : merged ? "merged into a live wave" : "wave A " + (a * 1000f).ToString("0.0") + " mm") + (pulseNote.Length > 0 ? ", " + pulseNote : "") + ", " + _count + " live");
        }

        static void UpdateStatus(ContactSettings s, float now)
        {
            if (s.Ripple <= 0f) { Status = "Ripple: off: strength 0"; return; }
            if (_lastAt < 0f) { Status = "Ripple: no impacts yet"; return; }
            Status = "Ripple: " + _count + (_count == 1 ? " wave" : " waves") + ", last by " + _lastBy + (_lastWhere.Length > 0 ? " (" + _lastWhere + ")" : "") + " " + _lastSpeed.ToString("0.0") + " m/s";
        }

        // ---- breast pulse ----

        static bool IsBreast(Transform bone, string zoneId)
        {
            if (!string.IsNullOrEmpty(zoneId) && zoneId.StartsWith("breast", StringComparison.Ordinal)) return true;
            if (bone == null) return false;
            string n = bone.name;
            return n.IndexOf("Breast", StringComparison.Ordinal) >= 0 && (n.StartsWith("Left_", StringComparison.Ordinal) || n.StartsWith("Right_", StringComparison.Ordinal));
        }

        static bool IsLeft(Transform bone, string zoneId)
        {
            if (bone != null && bone.name.StartsWith("Left_", StringComparison.Ordinal)) return true;
            return !string.IsNullOrEmpty(zoneId) && zoneId.EndsWith("_L", StringComparison.Ordinal);
        }

        static bool IsRight(Transform bone, string zoneId)
        {
            if (bone != null && bone.name.StartsWith("Right_", StringComparison.Ordinal)) return true;
            return !string.IsNullOrEmpty(zoneId) && zoneId.EndsWith("_R", StringComparison.Ordinal);
        }

        static bool ForceField()
        {
            Type t = VrHands.DynamicBoneType;
            if (t == null) return false;
            if (t != _fType)
            {
                _fType = t;
                _fForce = t.GetField("m_Force", Any);
                if (_fForce != null && _fForce.FieldType != typeof(Vector3)) _fForce = null;
                if (_fForce == null && !_fWarned) { _fWarned = true; Plugin.Log.LogWarning("Ripple: the game's DynamicBone has no m_Force (Vector3); no breast pulse"); }
            }
            return _fForce != null;
        }

        // Writes base + dir * 0.006 * I into m_Force of each breast DynamicBone of that side for 0.12 s. Returns how many.
        static int PulseBreasts(bool left, Vector3 dir, float I, float now)
        {
            if (!ForceField() || !Finite(dir)) return 0;
            VrHands.BreastDbs(_dbs);
            int n = 0;
            for (int i = 0; i < _dbs.Count; i++)
            {
                var db = _dbs[i];
                var root = VrHands.DynamicBoneRoot(db);
                if (db == null || root == null) continue;
                string rn = root.name;
                if (left ? !rn.StartsWith("Left_", StringComparison.Ordinal) : !rn.StartsWith("Right_", StringComparison.Ordinal)) continue;
                Pulse pu = null;
                for (int k = 0; k < _pulses.Count; k++) if (_pulses[k].Db == db) { pu = _pulses[k]; break; }
                if (pu == null) { pu = new Pulse { Db = db }; _pulses.Add(pu); }
                Vector3 cur = (Vector3)_fForce.GetValue(db);
                // the game (or another system) changed it since our write: that is the new base, like Body's trackers
                if (!pu.Active || cur != pu.Ours) pu.Base = cur;
                pu.Ours = pu.Base + dir * (PulseForce * I);
                pu.Until = now + PulseSeconds;
                pu.Active = true;
                _fForce.SetValue(db, pu.Ours);
                n++;
            }
            return n;
        }

        static void ServicePulses(float now)
        {
            for (int i = _pulses.Count - 1; i >= 0; i--)
            {
                var pu = _pulses[i];
                if (!pu.Active) continue;
                if (pu.Db == null) { _pulses.RemoveAt(i); continue; }
                if (now < pu.Until) continue;
                pu.Active = false;
                try
                {
                    Vector3 cur = (Vector3)_fForce.GetValue(pu.Db);
                    if (cur == pu.Ours) _fForce.SetValue(pu.Db, pu.Base);   // still ours: put the game's value back
                }
                catch (Exception e) { Plugin.Log.LogWarning("Ripple: restoring a breast bone's force failed: " + e.Message); }
            }
        }

        static void RestorePulses()
        {
            for (int i = 0; i < _pulses.Count; i++)
            {
                var pu = _pulses[i];
                if (!pu.Active || pu.Db == null || _fForce == null) continue;
                pu.Active = false;
                try
                {
                    Vector3 cur = (Vector3)_fForce.GetValue(pu.Db);
                    if (cur == pu.Ours) _fForce.SetValue(pu.Db, pu.Base);
                }
                catch { }
            }
            _pulses.Clear();
        }

        static bool Finite(Vector3 v)
        {
            return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
        }
    }
}
