using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // Secondary animation: her limbs, head and spine follow the animation through damped springs (overlap, follow-through).
    // Embody is ignored on purpose: it drives HIS arms, not hers (the Embody field is kept for the integrator's signature).
    internal class OverlapSettings { public float Amount, Arms = 1f, Legs = 1f, Head = 1f, Spine = 1f; public Embody Embody; }   // Embody: unused here

    // Runs in Body.ApplyAll after Apply(c) and before Soft.Simulate, i.e. after the animation wrote this frame and before LateUpdate scripts.
    //
    // Per bone: target = the animation's localRotation this frame. If the bone still holds exactly what we wrote last frame, the
    // animation did not write it this frame, so the previous target is kept. State is a second-order system on the rotation error
    // (omega += (K*theta*axis - C*omega)*h; q = exp(omega*h)*q) in fixed substeps of at most 1/120 s, the target interpolated across
    // the substeps. The written rotation is slerp(target, state, w), w = Amount * region * (1 - contact), the lag clamped to 12 deg.
    //
    // Her capsule names read from PhysicsHands (HerDefs): upperarm_L/R, forearm_L/R (arms), thigh_L/R, calf_L/R, head, neck, chest, spine.
    // Contact fades (0.15 s): arms by TouchingHer("upperarm_X"/"forearm_X"); legs by Soft.Touching("thighOut_X"/"thighIn_X"/"glute_X");
    // head by TouchingHer("head"/"neck"); spine by TouchingHer("spine"/"chest") or Soft.Touching("belly") (small addition to the spec,
    // so a held head or torso is not wobbled under the hand).
    internal class Overlap
    {
        public string Status = "";

        const float Step = 1f / 120f;
        const float Zeta = 0.7f;
        const float FadeTime = 0.15f;
        const float ClampDot = 0.99452f;            // cos(6 deg): |dot| below it = more than 12 deg between two rotations
        const float ClampDeg = 12f;
        const float ResetPos = 0.3f, ResetAngle = 60f, ResetDt = 0.1f;
        const float SameEps = 1e-6f;

        // groups: 0 arm L, 1 arm R, 2 leg L, 3 leg R, 4 head, 5 spine; region (weight slot): arms 0, legs 1, head 2, spine 3
        static readonly string[] GroupLabel = { "arm L", "arm R", "thigh L", "thigh R", "head", "spine" };
        static readonly int[] GroupRegion = { 0, 0, 1, 1, 2, 3 };

        struct Spec { public string Name; public int Group; public float F; public Spec(string n, int g, float f) { Name = n; Group = g; F = f; } }
        static readonly Spec[] Specs =
        {
            new Spec("Shoulder_L", 0, 4f), new Spec("Elbow_L", 0, 3f), new Spec("Wrist_L", 0, 2.5f),
            new Spec("Shoulder_R", 1, 4f), new Spec("Elbow_R", 1, 3f), new Spec("Wrist_R", 1, 2.5f),
            new Spec("Hip_L", 2, 4f), new Spec("Knee_L", 2, 3f), new Spec("Ankle_L", 2, 2.5f),
            new Spec("Hip_R", 3, 4f), new Spec("Knee_R", 3, 3f), new Spec("Ankle_R", 3, 2.5f),
            new Spec("Spine1_M", 5, 4f), new Spec("Chest_M", 5, 3f),
            new Spec("Neck_M", 4, 4f), new Spec("Head_M", 4, 2.5f),
        };

        class B
        {
            public Transform T; public int Group; public float K, C;
            public Quaternion Target, Prev, Qs, LastWrite; public Vector3 Omega, LastPos;
            public bool Init, HasLast;
        }

        readonly List<B> _bones = new List<B>(16);
        Transform _root;
        Character _her;
        readonly float[] _contact = new float[6];
        readonly bool[] _touched = new bool[6];
        readonly float[] _region = new float[4];
        readonly bool[] _loggedFade = new bool[6];
        readonly bool[] _loggedReset = new bool[3];
        bool _loggedDriven;
        int _sig = -1;
        readonly StringBuilder _sb = new StringBuilder(128);

        void Build(Character her)
        {
            _bones.Clear();
            _root = her.Root; _her = her;
            for (int i = 0; i < _contact.Length; i++) _contact[i] = 0f;
            _loggedDriven = false; _sig = -1;
            Dictionary<string, Transform> scan = null;
            var missing = new StringBuilder();
            for (int i = 0; i < Specs.Length; i++)
            {
                var sp = Specs[i];
                Transform t;
                if (!her.Bones.TryGetValue(sp.Name, out t) || t == null)
                {
                    if (scan == null)
                    {
                        scan = new Dictionary<string, Transform>();
                        if (her.Root != null) foreach (var x in her.Root.GetComponentsInChildren<Transform>(true)) if (!scan.ContainsKey(x.name)) scan[x.name] = x;
                    }
                    scan.TryGetValue(sp.Name, out t);
                }
                if (t == null) { missing.Append(missing.Length > 0 ? ", " : "").Append(sp.Name); continue; }
                float w = 2f * Mathf.PI * sp.F;
                _bones.Add(new B { T = t, Group = sp.Group, K = w * w, C = 2f * Zeta * w });
            }
            if (missing.Length > 0) Plugin.Log.LogInfo("Overlap: bones not found, skipped: " + missing);
        }

        public void Update(Character her, OverlapSettings s, float dt)
        {
            if (her == null || her.Root == null || s == null) { Release(); return; }
            if (!ReferenceEquals(her.Root, _root) || !ReferenceEquals(her, _her)) { Release(); Build(her); }
            for (int i = 0; i < _bones.Count; i++) if (_bones[i].T == null) { Release(); Build(her); break; }

            _region[0] = Mathf.Clamp01(s.Amount) * Mathf.Clamp01(s.Arms);
            _region[1] = Mathf.Clamp01(s.Amount) * Mathf.Clamp01(s.Legs);
            _region[2] = Mathf.Clamp01(s.Amount) * Mathf.Clamp01(s.Head);
            _region[3] = Mathf.Clamp01(s.Amount) * Mathf.Clamp01(s.Spine);

            // Contact per group (only for regions that are on); fade in 0.15 s.
            for (int g = 0; g < 6; g++)
            {
                bool t = false;
                if (_region[GroupRegion[g]] > 0f) t = Touch(her, g);
                _touched[g] = t;
                float c = _contact[g];
                if (dt > 0f) c = t ? Mathf.Min(1f, c + dt / FadeTime) : Mathf.Max(0f, c - dt / FadeTime);
                _contact[g] = c;
                if (t && !_loggedFade[g]) { _loggedFade[g] = true; Plugin.Log.LogInfo("Overlap: fading " + GroupLabel[g] + " (hand/body contact)"); }
            }

            bool reset = dt > ResetDt;
            if (reset && !_loggedReset[0]) { _loggedReset[0] = true; Plugin.Log.LogInfo("Overlap: reset, frame time " + dt.ToString("0.000") + " s"); }
            int n = dt > 0f ? Mathf.Max(1, Mathf.CeilToInt(dt / Step - 1e-3f)) : 0;
            float h = n > 0 ? dt / n : 0f;
            n = Mathf.Min(n, 12);

            int driven = 0;
            for (int i = 0; i < _bones.Count; i++)
            {
                var b = _bones[i];
                var tr = b.T;
                float wReq = _region[GroupRegion[b.Group]];
                Quaternion cur = tr.localRotation;
                if (wReq <= 0f)
                {
                    // Off for this bone: if the animation did not rewrite it, put the animation's rotation back, once.
                    if (b.HasLast && Same(cur, b.LastWrite)) tr.localRotation = b.Target;
                    b.HasLast = false; b.Init = false;
                    continue;
                }

                // Target: the animation's rotation, unless our last write is still there (the animation didn't write the bone).
                bool animWrote = !(b.HasLast && Same(cur, b.LastWrite));
                Vector3 pos = tr.position;
                if (b.Init)
                {
                    if (animWrote) { b.Prev = b.Target; b.Target = cur; } else b.Prev = b.Target;
                    int why = 0;
                    if (reset) why = 1;
                    else if ((pos - b.LastPos).sqrMagnitude > ResetPos * ResetPos) why = 2;
                    else if (Quaternion.Angle(b.Prev, b.Target) > ResetAngle) why = 3;
                    if (why != 0)
                    {
                        if (why > 1 && !_loggedReset[why - 1]) { _loggedReset[why - 1] = true; Plugin.Log.LogInfo("Overlap: reset on " + tr.name + ": " + (why == 2 ? "moved more than 0.3 m in one frame" : "turned more than 60 deg in one frame")); }
                        b.Init = false;
                    }
                }
                if (!b.Init)
                {
                    if (animWrote) b.Target = cur;
                    b.Prev = b.Target; b.Qs = b.Target; b.Omega = Vector3.zero; b.Init = true;
                }
                b.LastPos = pos;

                // Substeps with the target interpolated from last frame's to this frame's.
                for (int k = 1; k <= n; k++)
                {
                    Quaternion tk = n == 1 ? b.Target : Quaternion.Slerp(b.Prev, b.Target, (float)k / n);
                    Quaternion d = tk * Quaternion.Inverse(b.Qs);
                    if (d.w < 0f) { d.x = -d.x; d.y = -d.y; d.z = -d.z; d.w = -d.w; }
                    float ang; Vector3 ax;
                    d.ToAngleAxis(out ang, out ax);
                    float th = ang * Mathf.Deg2Rad;
                    if (th > 1e-6f && !float.IsNaN(ax.x)) b.Omega += (b.K * th * ax - b.C * b.Omega) * h;
                    else b.Omega -= b.C * b.Omega * h;
                    float sp = b.Omega.magnitude;
                    if (sp * h > 1e-8f)
                    {
                        Quaternion r = Quaternion.AngleAxis(sp * h * Mathf.Rad2Deg, b.Omega / sp);
                        b.Qs = r * b.Qs;
                        float m = Mathf.Sqrt(b.Qs.x * b.Qs.x + b.Qs.y * b.Qs.y + b.Qs.z * b.Qs.z + b.Qs.w * b.Qs.w);
                        if (m > 1e-6f) { float im = 1f / m; b.Qs.x *= im; b.Qs.y *= im; b.Qs.z *= im; b.Qs.w *= im; }
                    }
                    // Lag clamp (12 deg) on the state itself so it cannot wind up beyond it.
                    float dot = Mathf.Abs(Quaternion.Dot(tk, b.Qs));
                    if (dot < ClampDot) b.Qs = Quaternion.RotateTowards(tk, b.Qs, ClampDeg);
                }
                if (float.IsNaN(b.Qs.x) || float.IsNaN(b.Qs.w) || float.IsNaN(b.Omega.x)) { b.Qs = b.Target; b.Omega = Vector3.zero; }

                float w = wReq * (1f - _contact[b.Group]);
                Quaternion o = w >= 0.9999f ? b.Qs : (w <= 0f ? b.Target : Quaternion.Slerp(b.Target, b.Qs, w));
                if (!Same(o, cur)) tr.localRotation = o;
                b.LastWrite = o; b.HasLast = true;
                driven++;
            }

            if (driven > 0 && !_loggedDriven)
            {
                _loggedDriven = true;
                _sb.Length = 0;
                for (int i = 0; i < _bones.Count; i++) _sb.Append(i > 0 ? ", " : "").Append(_bones[i].T.name);
                Plugin.Log.LogInfo("Overlap: driving " + driven + " bones: " + _sb);
            }
            SetStatus(driven);
        }

        bool Touch(Character her, int g)
        {
            switch (g)
            {
                case 0: return PhysicsHands.TouchingHer("upperarm_L") || PhysicsHands.TouchingHer("forearm_L");
                case 1: return PhysicsHands.TouchingHer("upperarm_R") || PhysicsHands.TouchingHer("forearm_R");
                case 2: return her.Soft != null && (her.Soft.Touching("thighOut_L") || her.Soft.Touching("thighIn_L") || her.Soft.Touching("glute_L"));
                case 3: return her.Soft != null && (her.Soft.Touching("thighOut_R") || her.Soft.Touching("thighIn_R") || her.Soft.Touching("glute_R"));
                case 4: return PhysicsHands.TouchingHer("head") || PhysicsHands.TouchingHer("neck");
                default: return PhysicsHands.TouchingHer("spine") || PhysicsHands.TouchingHer("chest") || (her.Soft != null && her.Soft.Touching("belly"));
            }
        }

        static bool Same(Quaternion a, Quaternion b)
        {
            return Mathf.Abs(a.x - b.x) < SameEps && Mathf.Abs(a.y - b.y) < SameEps && Mathf.Abs(a.z - b.z) < SameEps && Mathf.Abs(a.w - b.w) < SameEps;
        }

        // Rebuilds the status string only when something visible changed (no per-frame allocation).
        void SetStatus(int driven)
        {
            int fade = 0;
            for (int g = 0; g < 6; g++) if (_contact[g] > 0.01f && _region[GroupRegion[g]] > 0f) fade |= 1 << g;
            int sig = driven | (Mathf.RoundToInt(_region[0] * 10f) << 8) | (Mathf.RoundToInt(_region[1] * 10f) << 12) | (Mathf.RoundToInt(_region[2] * 10f) << 16)
                    | (Mathf.RoundToInt(_region[3] * 10f) << 20) | (fade << 24);
            if (sig == _sig) return;
            _sig = sig;
            if (driven == 0) { Status = _bones.Count == 0 ? "off: no chain bones found" : "off (Amount 0)"; return; }
            _sb.Length = 0;
            _sb.Append("on: ").Append(driven).Append(" bones (arms ").Append(_region[0].ToString("0.0")).Append(", legs ").Append(_region[1].ToString("0.0"))
               .Append(", head ").Append(_region[2].ToString("0.0")).Append(", spine ").Append(_region[3].ToString("0.0")).Append(')');
            bool first = true;
            for (int g = 0; g < 6; g++)
                if ((fade & (1 << g)) != 0) { _sb.Append(first ? "; faded: " : ", ").Append(GroupLabel[g]).Append(" contact"); first = false; }
            Status = _sb.ToString();
        }

        // Gives back the animation's rotation on bones the animation did not rewrite, forgets everything.
        public void Release()
        {
            for (int i = 0; i < _bones.Count; i++)
            {
                var b = _bones[i];
                if (b.T != null && b.HasLast && Same(b.T.localRotation, b.LastWrite)) b.T.localRotation = b.Target;
            }
            _bones.Clear();
            _root = null; _her = null; _sig = -1;
            Status = "";
        }
    }
}
