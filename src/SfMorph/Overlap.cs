using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // Secondary animation: her limbs, head and spine follow the animation through damped springs (overlap, follow-through).
    // Embody is ignored on purpose: it drives HIS arms, not hers (the Embody field is kept for the integrator's signature).
    internal class OverlapSettings
    {
        public float Amount, Arms = 1f, Legs = 1f, Head = 1f, Spine = 1f; public Embody Embody;   // Embody: unused here
        // 0.7.7 (v2): ripple 0..1; bounce per region (0 arms, 1 legs, 2 head, 3 spine) 0..1; planted lock; limb push from the physics
        // hands 0..1; Caps = Body.Contacts() this frame (partner/hers capsules for the planted test; null when no hands are tracked)
        public float Ripple = 0.5f, LimbPush = 0.5f;
        public readonly float[] Bounce = { 0.5f, 0.4f, 0.6f, 0.3f };
        public bool PlantedLock = true;
        public List<SoftCapsule> Caps;
    }

    // Runs in Body.ApplyAll after Apply(c) and before Soft.Simulate, i.e. after the animation wrote this frame and before LateUpdate scripts.
    //
    // v1 (still the core): per bone, target = the animation's localRotation this frame. If the bone still holds exactly what we wrote
    // last frame, the animation did not write it this frame, so the previous target is kept. State is a second-order system on the
    // rotation error (omega += (K*theta*axis - C*omega)*h; q = exp(omega*h)*q) in fixed substeps of at most 1/120 s, the target
    // interpolated across the substeps. The written rotation is slerp(target, state, w), w = Amount * region * (1 - contact).
    //
    // v2 (0.7.7), on top of that:
    //  - Bones are processed parents first (torso, head, arms, legs). RIPPLE: before the substeps each bone gets a kick from its
    //    parent's world angular acceleration (the parent was already written this frame), so a fast torso turn swings the head and arms
    //    a little later and the delay accumulates link by link instead of every joint wobbling on its own.
    //  - Frequency, damping and the lag clamp are per region: Bounce 0..1 gives Zeta lerp(1.0, 0.35) and a frequency scale lerp(1.4, 0.7).
    //  - PLANTED LOCK: a hand/foot that stands still in the animation (or still relative to a partner/her own capsule it is near) is
    //    pinned to the animation's position by a two-bone IK (Ik.TwoBone) after the springs, so the elbow/knee swings and the
    //    hand/foot stays put (a hand resting on him does not slide when the arm lags).
    //  - LIMB PUSH: a hand (physics hand, hand model or partner; HerBody.PushOn) pushes any of her segments: a torque about the
    //    bones that carry it (PushMaps) drives a second spring state (Qp) that is multiplied onto the written rotation. 0.7.8: every
    //    bone but the wrists and ankles pushes, with per-region inertia and clamp, and a tanh knee on the torque so a hand held
    //    inside her settles short of the hard stop. The contact fade removes only the lag term, never the push.
    //
    // Her segment names read from HerBody (HerBody.SegNames): upperarm_L/R, forearm_L/R (arms), thigh_L/R, calf_L/R, head, neck, chest, spine.
    // Contact fades (0.15 s): arms by TouchingHer("upperarm_X"/"forearm_X"); legs by Soft.Touching("thighOut_X"/"thighIn_X"/"glute_X");
    // head by TouchingHer("head"/"neck"); spine by TouchingHer("spine"/"chest") or Soft.Touching("belly") (small addition to the spec,
    // so a held head or torso is not wobbled under the hand).
    internal class Overlap
    {
        public string Status = "";

        const float Step = 1f / 120f;
        const float FadeTime = 0.15f;
        const float ResetPos = 0.3f, ResetAngle = 60f, ResetDt = 0.1f;
        const float SameEps = 1e-6f;
        const float MaxOmega = 40f;                 // rad/s, hard cap on a spring state's angular velocity (safety, never reached normally)

        // ripple kick limits: parent angular velocity 20 rad/s, angular acceleration 300 rad/s^2
        const float RippleOmegaMax = 20f, RippleAlphaMax = 300f;

        // planted lock
        const float PlantDist = 0.08f, StillSpeed = 0.04f, StillHold = 0.08f, PlantFade = 0.15f;

        // limb push: per region {arms, legs, head, spine} inertia (kg*m^2 equivalent) and hard-stop angle; torque cap and rate cap are common
        // torque cap per region: legs and spine are heavier (a press of 40 N at 25 cm on the chest is 10 N*m), the head lighter
        static readonly float[] PushTauMaxR = { 6f, 10f, 3f, 10f };
        const float PushOmegaMax = 20f;
        static readonly float[] PushInertiaR = { 0.01f, 0.03f, 0.015f, 0.06f };
        static readonly float[] PushClampDegR = { 30f, 20f, 15f, 10f };
        // Knee: the push spring is  alpha = tau/I - K*theta - C*omega  (the kick adds tau/I*dt to OmegaP, SpringStep adds -K*theta), so
        // the resting angle under a steady torque is theta = tau/(I*K). The torque is run through knee*tanh(tau/knee) with
        // knee = 0.8 * clamp[rad] * I * K, which makes theta = 0.8*clamp*tanh(tau/knee): linear for small pushes, asymptote 0.8 clamp,
        // never the hard stop. Arm (I 0.01, K 392 at Elbow_X defaults): knee 1.64 N*m; 60 N on a forearm (torque capped at PushTauMaxR 6)
        // -> tanh(3.7) = 1.00 -> 24 deg of 30; a 5 N brush (0.75 N*m at 0.15 m) -> tanh(0.46) = 0.43 -> 10 deg.
        const float KneeFrac = 0.8f;

        // Segment -> bone torque shares (bone names are Overlap bones, segments HerBody.SegNames). Torque about the bone's origin:
        // tau = Share * cross(point - bone.position, force on her). pelvis, butt_L/R push nothing: Root_M is never moved.
        struct PushMap { public string Seg, Bone; public float Share; public PushMap(string s, string b, float f) { Seg = s; Bone = b; Share = f; } }
        static readonly PushMap[] PushMaps =
        {
            new PushMap("upperarm_L", "Shoulder_L", 1f), new PushMap("forearm_L", "Elbow_L", 1f), new PushMap("forearm_L", "Shoulder_L", 0.5f),
            new PushMap("upperarm_R", "Shoulder_R", 1f), new PushMap("forearm_R", "Elbow_R", 1f), new PushMap("forearm_R", "Shoulder_R", 0.5f),
            new PushMap("thigh_L", "Hip_L", 1f), new PushMap("calf_L", "Knee_L", 1f), new PushMap("calf_L", "Hip_L", 0.5f),
            new PushMap("thigh_R", "Hip_R", 1f), new PushMap("calf_R", "Knee_R", 1f), new PushMap("calf_R", "Hip_R", 0.5f),
            new PushMap("head", "Head_M", 1f), new PushMap("head", "Neck_M", 0.5f), new PushMap("neck", "Neck_M", 1f),
            new PushMap("chest", "Chest_M", 1f), new PushMap("chest", "Spine1_M", 0.5f), new PushMap("spine", "Spine1_M", 1f),
            new PushMap("breast_L", "Chest_M", 0.5f), new PushMap("breast_L", "Spine1_M", 0.25f),
            new PushMap("breast_R", "Chest_M", 0.5f), new PushMap("breast_R", "Spine1_M", 0.25f),
        };
        struct PushRes { public int Seg; public B Bone; public float Share; }

        // groups: 0 arm L, 1 arm R, 2 leg L, 3 leg R, 4 head, 5 spine; region (weight slot): arms 0, legs 1, head 2, spine 3
        static readonly string[] GroupLabel = { "arm L", "arm R", "thigh L", "thigh R", "head", "spine" };
        static readonly int[] GroupRegion = { 0, 0, 1, 1, 2, 3 };
        // per region {arms, legs, head, spine}
        static readonly float[] ClampDegR = { 15f, 12f, 20f, 8f };
        static readonly float[] RippleGainR = { 1.0f, 0.6f, 1.2f, 0.5f };
        static readonly float[] ClampDotR = MakeClampDot();   // |dot| below this = more than ClampDegR between two rotations
        static float[] MakeClampDot() { var a = new float[ClampDegR.Length]; for (int i = 0; i < a.Length; i++) a[i] = Mathf.Cos(ClampDegR[i] * 0.5f * Mathf.Deg2Rad); return a; }

        struct Spec { public string Name; public int Group; public float F; public Spec(string n, int g, float f) { Name = n; Group = g; F = f; } }
        // Parents first: the ripple of a bone reads its parent after that parent was written this frame.
        static readonly Spec[] Specs =
        {
            new Spec("Spine1_M", 5, 4f), new Spec("Chest_M", 5, 3f),
            new Spec("Neck_M", 4, 3f), new Spec("Head_M", 4, 2f),
            new Spec("Shoulder_L", 0, 4f), new Spec("Elbow_L", 0, 3f), new Spec("Wrist_L", 0, 2.5f),
            new Spec("Shoulder_R", 1, 4f), new Spec("Elbow_R", 1, 3f), new Spec("Wrist_R", 1, 2.5f),
            new Spec("Hip_L", 2, 4f), new Spec("Knee_L", 2, 3f), new Spec("Ankle_L", 2, 2.5f),
            new Spec("Hip_R", 3, 4f), new Spec("Knee_R", 3, 3f), new Spec("Ankle_R", 3, 2.5f),
        };

        // end effectors: arms (Shoulder/Elbow/Wrist) and legs (Hip/Knee/Ankle); index = group 0..3
        static readonly string[][] EffNames =
        {
            new[] { "Shoulder_L", "Elbow_L", "Wrist_L" }, new[] { "Shoulder_R", "Elbow_R", "Wrist_R" },
            new[] { "Hip_L", "Knee_L", "Ankle_L" }, new[] { "Hip_R", "Knee_R", "Ankle_R" },
        };
        static readonly string[] EffLabel = { "left hand", "right hand", "left foot", "right foot" };
        static readonly string[] EffShort = { "L hand", "R hand", "L foot", "R foot" };

        class B
        {
            public Transform T, P; public int Group; public float F, K, C;
            public Quaternion Target, Prev, Qs, LastWrite; public Vector3 Omega, LastPos;
            public bool Init, HasLast;
            // ripple: parent's world rotation last frame and its angular velocity; PState 0 none, 1 rotation known, 2 rotation and velocity known
            public Quaternion PW = Quaternion.identity; public Vector3 POmega; public int PState;
            // limb push (every bone but wrists/ankles): offset rotation in the parent's frame, its angular velocity, this frame's torque (world)
            public bool CanPush, PushLive; public Quaternion Qp = Quaternion.identity; public Vector3 OmegaP, TauW;
        }

        class Eff
        {
            public int Group; public B A, M, E; public bool On;
            public Vector3 AnimPos, AnimPrev; public Quaternion AnimRot;
            public bool HasPrev, AnimOk, Logged;
            public float StillT, Planted;
            public int NearIdx = -1; public Transform NearT; public Vector3 NearLoc; public bool NearHas; public string NearName;
        }

        readonly List<B> _bones = new List<B>(16);
        readonly Eff[] _effs = new Eff[4];
        Transform _root;
        Character _her;
        readonly float[] _contact = new float[6];
        readonly bool[] _touched = new bool[6];
        readonly bool[] _grpReset = new bool[6];
        readonly float[] _region = new float[4];
        readonly float[] _zeta = new float[4];
        readonly float[] _fs = new float[4];
        readonly bool[] _loggedFade = new bool[6];
        readonly bool[] _loggedReset = new bool[3];
        readonly bool[] _loggedPush = new bool[HerBody.SegNames.Length];
        float _ripple, _lp;
        uint _pushBits;                                   // bit per HerBody segment index pushed this frame
        PushRes[] _pushRes = new PushRes[0];              // PushMaps resolved to bones at build (missing bones skipped)
        // one PushOn per distinct segment per frame (stamped by _pushPass), cached for the maps that share it
        readonly int[] _pushStamp = new int[HerBody.SegNames.Length];
        readonly bool[] _pushHas = new bool[HerBody.SegNames.Length];
        readonly Vector3[] _pushPt = new Vector3[HerBody.SegNames.Length];
        readonly Vector3[] _pushF = new Vector3[HerBody.SegNames.Length];
        readonly string[] _pushBy = new string[HerBody.SegNames.Length];
        int _pushPass;
        bool _loggedDriven;
        long _sig = -1;
        readonly StringBuilder _sb = new StringBuilder(160);

        void Build(Character her)
        {
            _bones.Clear();
            for (int i = 0; i < _effs.Length; i++) _effs[i] = null;
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
                bool push = !sp.Name.StartsWith("Wrist_") && !sp.Name.StartsWith("Ankle_");
                _bones.Add(new B { T = t, P = t.parent, Group = sp.Group, F = sp.F, CanPush = push });
            }
            if (missing.Length > 0) Plugin.Log.LogInfo("Overlap: bones not found, skipped: " + missing);
            var res = new List<PushRes>(PushMaps.Length);
            for (int i = 0; i < PushMaps.Length; i++)
            {
                int si = Array.IndexOf(HerBody.SegNames, PushMaps[i].Seg);
                B pb = FindB(PushMaps[i].Bone);
                if (si >= 0 && pb != null && pb.CanPush) res.Add(new PushRes { Seg = si, Bone = pb, Share = PushMaps[i].Share });
            }
            _pushRes = res.ToArray();
            for (int i = 0; i < _pushStamp.Length; i++) { _pushStamp[i] = 0; _pushHas[i] = false; }
            for (int e = 0; e < _effs.Length; e++)
            {
                var n = EffNames[e];
                B a = FindB(n[0]), m = FindB(n[1]), d = FindB(n[2]);
                if (a != null && m != null && d != null) _effs[e] = new Eff { Group = e, A = a, M = m, E = d };
            }
        }

        B FindB(string name)
        {
            for (int i = 0; i < _bones.Count; i++) if (_bones[i].T.name == name) return _bones[i];
            return null;
        }

        public void Update(Character her, OverlapSettings s, float dt)
        {
            if (her == null || her.Root == null || s == null) { Release(); return; }
            if (!ReferenceEquals(her.Root, _root) || !ReferenceEquals(her, _her)) { Release(); Build(her); }
            for (int i = 0; i < _bones.Count; i++) if (_bones[i].T == null) { Release(); Build(her); break; }

            float amt = Mathf.Clamp01(s.Amount);
            _region[0] = amt * Mathf.Clamp01(s.Arms);
            _region[1] = amt * Mathf.Clamp01(s.Legs);
            _region[2] = amt * Mathf.Clamp01(s.Head);
            _region[3] = amt * Mathf.Clamp01(s.Spine);
            for (int r = 0; r < 4; r++)
            {
                float bo = Mathf.Clamp01(s.Bounce != null && r < s.Bounce.Length ? s.Bounce[r] : 0.5f);
                _zeta[r] = Mathf.Lerp(1.0f, 0.35f, bo);      // v1 used 0.7 everywhere
                _fs[r] = Mathf.Lerp(1.4f, 0.7f, bo);         // bouncier also means slower
            }
            _ripple = Mathf.Clamp01(s.Ripple);
            _lp = Mathf.Clamp01(s.LimbPush);
            _pushBits = 0u;

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
                _grpReset[g] = false;
            }

            bool reset = dt > ResetDt;
            if (reset && !_loggedReset[0]) { _loggedReset[0] = true; Plugin.Log.LogInfo("Overlap: reset, frame time " + dt.ToString("0.000") + " s"); }
            int n = dt > 0f ? Mathf.Min(12, Mathf.Max(1, Mathf.CeilToInt(dt / Step - 1e-3f))) : 0;
            float h = n > 0 ? dt / n : 0f;

            // The animation's end-effector poses, BEFORE any write this frame (the chain is still the animation's, with Body's scales),
            // and the planted detection on them.
            for (int e = 0; e < _effs.Length; e++)
            {
                var ef = _effs[e];
                if (ef == null) continue;
                Detect(ef, s, dt, reset);
            }

            // Limb push torques (every segment in PushMaps), from HerBody.PushOn (physics hands' contact force, or the geometric push of
            // hand models and partners). Independent of the overlap amount: with Overlap 0 a pushed limb still yields and springs back.
            bool pushable = _lp > 0f && HerBody.PushActive;
            if (pushable) PushPass();

            int driven = 0;
            for (int i = 0; i < _bones.Count; i++)
            {
                var b = _bones[i];
                var tr = b.T;
                int rg = GroupRegion[b.Group];
                float wReq = _region[rg];
                Quaternion cur = tr.localRotation;
                // A bone with a push in progress (or a torque this frame) keeps running at region weight 0: the lag term stays off (w = 0 below,
                // the written rotation is the animation's target) and only the push offset Qp is added on top. With no push and no lag the bone
                // is left alone (exactly the game's animation).
                bool pushWake = wReq <= 0f && b.CanPush && pushable && (b.PushLive || b.TauW.sqrMagnitude > 1e-8f);
                if (wReq <= 0f && !pushWake)
                {
                    // Off for this bone: if the animation did not rewrite it, put the animation's rotation back, once.
                    if (b.HasLast && Same(cur, b.LastWrite)) tr.localRotation = b.Target;
                    b.HasLast = false; b.Init = false; b.PState = 0;
                    b.Qp = Quaternion.identity; b.OmegaP = Vector3.zero; b.PushLive = false; b.TauW = Vector3.zero;
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
                    // every reset path zeroes the ripple and push state too
                    b.PState = 0; b.POmega = Vector3.zero;
                    b.Qp = Quaternion.identity; b.OmegaP = Vector3.zero; b.PushLive = false;
                    _grpReset[b.Group] = true;
                }
                b.LastPos = pos;

                float fw = 2f * Mathf.PI * b.F * _fs[rg];
                b.K = fw * fw; b.C = 2f * _zeta[rg] * fw;

                // Ripple: the parent's world angular acceleration, seen from the parent's frame, kicks this bone's state.
                if (wReq <= 0f) b.PState = 0;      // no ripple without the lag term
                else if (b.P != null)
                {
                    Quaternion pw = b.P.rotation;
                    if (n > 0)
                    {
                        if (b.PState >= 1 && !reset)
                        {
                            Quaternion dq = pw * Quaternion.Inverse(b.PW);
                            if (dq.w < 0f) { dq.x = -dq.x; dq.y = -dq.y; dq.z = -dq.z; dq.w = -dq.w; }
                            float ang; Vector3 ax;
                            dq.ToAngleAxis(out ang, out ax);
                            if (ang > ResetAngle || float.IsNaN(ax.x) || float.IsNaN(ang))
                            {
                                b.PState = 1; b.POmega = Vector3.zero;                    // the parent jumped: re-baseline, no kick
                            }
                            else
                            {
                                Vector3 om = ang > 1e-4f ? ax * (ang * Mathf.Deg2Rad / dt) : Vector3.zero;
                                float om2 = om.sqrMagnitude;
                                if (om2 > RippleOmegaMax * RippleOmegaMax) om *= RippleOmegaMax / Mathf.Sqrt(om2);
                                if (b.PState >= 2 && _ripple > 0f)
                                {
                                    Vector3 alpha = (om - b.POmega) / dt;
                                    float a2 = alpha.sqrMagnitude;
                                    if (a2 > RippleAlphaMax * RippleAlphaMax) alpha *= RippleAlphaMax / Mathf.Sqrt(a2);
                                    b.Omega -= (Quaternion.Inverse(pw) * alpha) * (_ripple * RippleGainR[rg] * dt);
                                }
                                b.POmega = om; b.PState = 2;
                            }
                        }
                        else b.PState = 1;
                        b.PW = pw;
                    }
                }

                // Push kick: torque (world) -> the parent's frame (Qp is premultiplied onto the bone's local rotation).
                bool pushOn = b.CanPush && _lp > 0f;
                float pInertia = PushInertiaR[rg], pClampDeg = PushClampDegR[rg];
                if (pushOn && b.TauW.sqrMagnitude > 1e-8f && b.P != null && n > 0)
                {
                    // the knee (see PushInertiaR): soft-saturate the torque at 0.8 of the clamp angle's equilibrium torque
                    float knee = Mathf.Deg2Rad * pClampDeg * KneeFrac * pInertia * b.K;
                    float tm = b.TauW.magnitude;
                    if (tm > 1e-6f && knee > 1e-6f) b.TauW *= knee * (float)Math.Tanh(tm / knee) / tm;
                    b.OmegaP += (Quaternion.Inverse(b.P.rotation) * b.TauW) * (dt / pInertia);
                    float po = b.OmegaP.magnitude;
                    if (po > PushOmegaMax) b.OmegaP *= PushOmegaMax / po;
                    b.PushLive = true;
                }
                b.TauW = Vector3.zero;
                if (!pushOn && b.PushLive) { b.Qp = Quaternion.identity; b.OmegaP = Vector3.zero; b.PushLive = false; }
                bool pushStep = b.PushLive;

                // Substeps with the target interpolated from last frame's to this frame's.
                float clampDot = ClampDotR[rg], clampDeg = ClampDegR[rg];
                for (int k = 1; k <= n; k++)
                {
                    Quaternion tk = n == 1 ? b.Target : Quaternion.Slerp(b.Prev, b.Target, (float)k / n);
                    SpringStep(ref b.Qs, ref b.Omega, tk, b.K, b.C, h);
                    // Lag clamp on the state itself so it cannot wind up beyond it.
                    float dot = Mathf.Abs(Quaternion.Dot(tk, b.Qs));
                    if (dot < clampDot) b.Qs = Quaternion.RotateTowards(tk, b.Qs, clampDeg);
                    if (pushStep) SpringStep(ref b.Qp, ref b.OmegaP, Quaternion.identity, b.K, b.C, h);
                }
                if (float.IsNaN(b.Qs.x) || float.IsNaN(b.Qs.w) || float.IsNaN(b.Omega.x)) { b.Qs = b.Target; b.Omega = Vector3.zero; }
                if (float.IsNaN(b.Qp.x) || float.IsNaN(b.Qp.w) || float.IsNaN(b.OmegaP.x)) { b.Qp = Quaternion.identity; b.OmegaP = Vector3.zero; b.PushLive = false; }
                if (b.PushLive)
                {
                    float pa = Quaternion.Angle(Quaternion.identity, b.Qp);
                    if (pa > pClampDeg) { b.Qp = Quaternion.RotateTowards(Quaternion.identity, b.Qp, pClampDeg); b.OmegaP = Vector3.zero; }   // no wind-up against the clamp
                    else if (pa < 0.05f && b.OmegaP.sqrMagnitude < 1e-4f) { b.Qp = Quaternion.identity; b.OmegaP = Vector3.zero; b.PushLive = false; }
                }
                if (float.IsNaN(b.PW.x) || float.IsNaN(b.POmega.x)) { b.PW = Quaternion.identity; b.POmega = Vector3.zero; b.PState = 0; }

                // Contact fades only the lag term; the push (if any) stays on top of it.
                float w = wReq * (1f - _contact[b.Group]);
                Quaternion o = w >= 0.9999f ? b.Qs : (w <= 0f ? b.Target : Quaternion.Slerp(b.Target, b.Qs, w));
                if (b.PushLive && _lp > 0f)
                    o = (_lp >= 0.9999f ? b.Qp : Quaternion.Slerp(Quaternion.identity, b.Qp, _lp)) * o;
                if (!Same(o, cur)) tr.localRotation = o;
                b.LastWrite = o; b.HasLast = true;
                if (wReq > 0f) driven++;
            }

            // Planted correction, after all springs (the chains' lag shows as the elbow/knee moving while the hand/foot stays).
            for (int e = 0; e < _effs.Length; e++)
            {
                var ef = _effs[e];
                if (ef == null || !ef.On) continue;
                if (_grpReset[ef.Group]) { ResetEff(ef); continue; }
                if (!ef.AnimOk || !(ef.Planted > 0.001f) || !ef.A.HasLast || !ef.M.HasLast || !ef.E.HasLast) continue;
                if (Ik.TwoBone(ef.A.T, ef.M.T, ef.E.T, ef.AnimPos, ef.AnimRot, ef.Planted))
                {
                    // the corrected rotations are what the bones hold now: the animation-wrote detection must compare against them
                    ef.A.LastWrite = ef.A.T.localRotation; ef.M.LastWrite = ef.M.T.localRotation; ef.E.LastWrite = ef.E.T.localRotation;
                }
            }

            if (driven > 0 && !_loggedDriven)
            {
                _loggedDriven = true;
                _sb.Length = 0;
                bool firstB = true;
                for (int i = 0; i < _bones.Count; i++)
                {
                    if (!(_region[GroupRegion[_bones[i].Group]] > 0f)) continue;     // the same test that counts `driven`
                    _sb.Append(firstB ? "" : ", ").Append(_bones[i].T.name); firstB = false;
                }
                Plugin.Log.LogInfo("Overlap: v2 (ripple, planted lock, push) driving " + driven + " bones: " + _sb);
            }
            SetStatus(driven);
        }

        // One second-order step of q toward tk (see the class comment); angular velocity capped, state renormalised.
        static void SpringStep(ref Quaternion q, ref Vector3 om, Quaternion tk, float K, float C, float h)
        {
            Quaternion d = tk * Quaternion.Inverse(q);
            if (d.w < 0f) { d.x = -d.x; d.y = -d.y; d.z = -d.z; d.w = -d.w; }
            float ang; Vector3 ax;
            d.ToAngleAxis(out ang, out ax);
            float th = ang * Mathf.Deg2Rad;
            if (th > 1e-6f && !float.IsNaN(ax.x)) om += (K * th * ax - C * om) * h;
            else om -= C * om * h;
            float sp = om.magnitude;
            if (sp > MaxOmega) { om *= MaxOmega / sp; sp = MaxOmega; }
            if (sp * h > 1e-8f)
            {
                Quaternion r = Quaternion.AngleAxis(sp * h * Mathf.Rad2Deg, om / sp);
                q = r * q;
                float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                if (m > 1e-6f) { float im = 1f / m; q.x *= im; q.y *= im; q.z *= im; q.w *= im; }
            }
        }

        // ---- planted lock ----

        static void ResetEff(Eff e)
        {
            e.Planted = 0f; e.StillT = 0f; e.HasPrev = false;
            e.NearIdx = -1; e.NearT = null; e.NearHas = false;
        }

        bool Wrote(B b)
        {
            return !(b.HasLast && Same(b.T.localRotation, b.LastWrite));
        }

        // Records the animation's pose of the end effector (before any write) and updates how planted it is.
        void Detect(Eff e, OverlapSettings s, float dt, bool reset)
        {
            e.AnimOk = false;
            e.On = _region[GroupRegion[e.Group]] > 0f && e.A.T != null && e.M.T != null && e.E.T != null;
            if (!e.On) { ResetEff(e); return; }
            // All three bones must come from the animation this frame, else the end position already contains our lag.
            if (!Wrote(e.A) || !Wrote(e.M) || !Wrote(e.E)) { ResetEff(e); return; }
            Vector3 p = e.E.T.position;
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)) { ResetEff(e); return; }
            e.AnimPrev = e.AnimPos; e.AnimPos = p; e.AnimRot = e.E.T.rotation;
            e.AnimOk = true;
            bool prevOk = e.HasPrev;
            if (reset || (prevOk && (e.AnimPos - e.AnimPrev).sqrMagnitude > ResetPos * ResetPos)) { ResetEff(e); e.HasPrev = true; return; }
            e.HasPrev = true;
            if (!s.PlantedLock) { e.Planted = 0f; e.StillT = 0f; e.NearIdx = -1; e.NearT = null; e.NearHas = false; return; }
            if (!(dt > 0f)) return;                         // paused: keep the state

            float speed = prevOk ? (e.AnimPos - e.AnimPrev).magnitude / dt : float.MaxValue;
            float rel = float.MaxValue;
            bool near = FindNear(e, s.Caps, e.AnimPos);
            if (near)
            {
                if (e.NearHas) rel = (e.AnimPos - e.NearT.TransformPoint(e.NearLoc)).magnitude / dt;   // motion relative to the capsule's frame
                e.NearLoc = e.NearT.InverseTransformPoint(e.AnimPos); e.NearHas = true;
            }
            bool onCap = near && rel < StillSpeed;
            bool now = onCap || speed < StillSpeed;
            e.StillT = now ? e.StillT + dt : 0f;
            float target = e.StillT >= StillHold ? 1f : 0f;
            e.Planted = Mathf.MoveTowards(e.Planted, target, dt / PlantFade);
            if (target > 0f && !e.Logged)
            {
                e.Logged = true;
                Plugin.Log.LogInfo("Overlap: " + EffLabel[e.Group] + " planted (" + (onCap ? "on " + e.NearName + ", " + rel.ToString("0.00") : "still, " + (speed < 1e5f ? speed : 0f).ToString("0.00")) + " m/s)");
            }
        }

        static bool Eligible(SoftCapsule c, Eff e)
        {
            if (c.T == null) return false;
            if (c.Src != null && !c.Src.Live) return false;
            string k = c.Kind;
            if (k == "partner") return true;
            if (k == "thighL" || k == "thighR" || k == "forearm" || k == "hand")
                return !c.T.IsChildOf(e.A.T);     // her own limb that this effector hangs from is not a surface it can rest on
            return false;
        }

        static float SurfDist(SoftCapsule c, Vector3 p)
        {
            Vector3 a = c.T.TransformPoint(c.LocalA), b = c.T.TransformPoint(c.LocalB);
            Vector3 ab = b - a;
            float l2 = ab.sqrMagnitude;
            float t = l2 > 1e-10f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / l2) : 0f;
            return (p - (a + ab * t)).magnitude - c.Radius;
        }

        // The partner/hers capsule the effector is within PlantDist of (the previous one is kept while it stays that close).
        bool FindNear(Eff e, List<SoftCapsule> caps, Vector3 p)
        {
            if (caps == null) { e.NearIdx = -1; e.NearT = null; e.NearHas = false; return false; }
            int cnt = caps.Count;
            if (e.NearIdx >= 0 && e.NearIdx < cnt && e.NearT != null && ReferenceEquals(caps[e.NearIdx].T, e.NearT) && Eligible(caps[e.NearIdx], e)
                && SurfDist(caps[e.NearIdx], p) <= PlantDist) return true;
            int bi = -1; float best = PlantDist;
            for (int i = 0; i < cnt; i++)
            {
                var c = caps[i];
                if (!Eligible(c, e)) continue;
                float d = SurfDist(c, p);
                if (d <= best) { best = d; bi = i; }
            }
            if (bi < 0) { e.NearIdx = -1; e.NearT = null; e.NearHas = false; return false; }
            var cb = caps[bi];
            if (!ReferenceEquals(cb.T, e.NearT)) e.NearHas = false;          // a different frame: no relative motion known yet
            e.NearIdx = bi; e.NearT = cb.T;
            e.NearName = cb.Src != null && !string.IsNullOrEmpty(cb.Src.Name) ? cb.Src.Name : (cb.Owner ?? cb.Kind);
            return true;
        }

        // ---- limb push ----

        // PushMaps: for each (segment, bone) the segment's push as a torque about the bone's origin. One PushOn per distinct segment per frame.
        void PushPass()
        {
            _pushPass++;
            for (int i = 0; i < _pushRes.Length; i++)
            {
                var r = _pushRes[i];
                int sg = r.Seg;
                if (_pushStamp[sg] != _pushPass)
                {
                    _pushStamp[sg] = _pushPass;
                    Vector3 pt, f; string by;
                    bool ok = HerBody.PushOn(sg, out pt, out f, out by) && Ok(pt) && Ok(f);
                    _pushHas[sg] = ok; _pushPt[sg] = pt; _pushF[sg] = f; _pushBy[sg] = by;
                    if (ok) NotePush(sg, Vector3.Cross(pt - r.Bone.T.position, f).magnitude, by);   // first map of a segment = its main bone
                }
                if (!_pushHas[sg]) continue;
                AddTau(r.Bone, Vector3.Cross(_pushPt[sg] - r.Bone.T.position, _pushF[sg]) * r.Share);
            }
        }

        static bool Ok(Vector3 v) { return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)); }

        static void AddTau(B b, Vector3 tau)
        {
            b.TauW += tau;
            float m = b.TauW.magnitude, cap = PushTauMaxR[GroupRegion[b.Group]];
            if (m > cap) b.TauW *= cap / m;
        }

        void NotePush(int seg, float nm, string by)
        {
            _pushBits |= 1u << seg;
            if (_loggedPush[seg]) return;
            _loggedPush[seg] = true;
            Plugin.Log.LogInfo("Overlap: " + HerBody.SegNames[seg] + " pushed by " + by + " (" + nm.ToString("0.0") + " N*m)");
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
            int planted = 0;
            for (int e = 0; e < _effs.Length; e++) if (_effs[e] != null && _effs[e].On && _effs[e].Planted > 0.5f) planted |= 1 << e;
            uint lo = (uint)driven | ((uint)Mathf.RoundToInt(_region[0] * 10f) << 8) | ((uint)Mathf.RoundToInt(_region[1] * 10f) << 12) | ((uint)Mathf.RoundToInt(_region[2] * 10f) << 16)
                    | ((uint)Mathf.RoundToInt(_region[3] * 10f) << 20) | ((uint)fade << 24);
            long sig = (long)lo | ((long)planted << 32) | ((long)Mathf.RoundToInt(_ripple * 10f) << 36) | ((long)_pushBits << 41);
            if (sig == _sig) return;
            _sig = sig;
            if (driven == 0)
            {
                Status = _bones.Count == 0 ? "off: no chain bones found" : "off (Amount 0)";
                if (_pushBits != 0u)
                {
                    _sb.Length = 0; _sb.Append(Status);
                    bool f1 = true;
                    for (int sg = 0; sg < _pushStamp.Length; sg++)
                        if ((_pushBits & (1u << sg)) != 0u) { _sb.Append(f1 ? "; pushed: " : ", ").Append(HerBody.SegNames[sg]); f1 = false; }
                    Status = _sb.ToString();
                }
                return;
            }
            _sb.Length = 0;
            _sb.Append("on: ").Append(driven).Append(" bones (arms ").Append(_region[0].ToString("0.0")).Append(", legs ").Append(_region[1].ToString("0.0"))
               .Append(", head ").Append(_region[2].ToString("0.0")).Append(", spine ").Append(_region[3].ToString("0.0")).Append("), ripple ").Append(_ripple.ToString("0.0"));
            bool first = true;
            for (int e = 0; e < _effs.Length; e++)
                if ((planted & (1 << e)) != 0) { _sb.Append(first ? "; planted: " : ", ").Append(EffShort[e]); first = false; }
            first = true;
            for (int sg = 0; sg < _pushStamp.Length; sg++)
                if ((_pushBits & (1u << sg)) != 0u) { _sb.Append(first ? "; pushed: " : ", ").Append(HerBody.SegNames[sg]); first = false; }
            first = true;
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
            for (int i = 0; i < _effs.Length; i++) _effs[i] = null;
            _root = null; _her = null; _sig = -1;
            _pushRes = new PushRes[0];
            Status = "";
        }
    }
}
