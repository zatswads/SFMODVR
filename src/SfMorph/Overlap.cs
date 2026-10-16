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
        // 0.8.0: multiplier of the lag on the OUTPUT only (1..4): the spring states are unchanged, so it can't destabilise them
        public float Strength = 1f;
        // 0.7.7 (v2): ripple 0..1; bounce per region (0 arms, 1 legs, 2 head, 3 spine) 0..1; planted lock; limb yield from the hands
        // 0..1 (LimbPush: 0 off, 0.5 natural, 1 twice as far); Caps = Body.Contacts() this frame (partner/hers capsules for the planted
        // test; null when no hands are tracked)
        public float Ripple = 0.5f, LimbPush = 0.5f;
        public readonly float[] Bounce = { 0.5f, 0.4f, 0.6f, 0.3f };
        public bool PlantedLock = true;
        // 0.8.9 (user): [Hands] YieldLock: while a hand pushes one of her arms/legs, its hand/foot stays where the animation puts it (the
        // elbow/knee and shoulder/hip take the whole yield), with or without PlantedLock
        public bool YieldLock;
        public List<SoftCapsule> Caps;
    }

    // Runs in Body.ApplyAll after Apply(c) and HerBody.Update and before Soft.Simulate, i.e. after the animation wrote this frame and before
    // LateUpdate scripts.
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
    //  - PLANTED LOCK (v2, 0.8.0): every hand and foot is pinned to the animation's position by a two-bone IK (Ik.TwoBone) after the
    //    springs and the yield, whenever OUR writes moved it (its chain's lag, the yield, a kick, the torso's lag carried down), so the
    //    elbow/knee swings and the hand/foot stays put. It is released only while it swings fast: the least speed of the effector over
    //    the world, her pelvis, the partner rigs and the nearest capsule decides (< 0.15 m/s pinned, > 0.5 m/s free, hysteresis).
    //  - LIMB YIELD (0.8.0, replaces the 0.7.7-0.7.9 push springs): a hand that presses into one of her segments (HerBody.YieldOn: the
    //    penetration of the hand into her ANIMATION-pose skin, measured before this class writes anything, see Unwind) moves the contact
    //    point aside by a soft-capped displacement; the bones that carry the segment (YieldMaps) turn to get it there. Each bone follows
    //    its target rotation vector through a critically damped filter (exact closed form, any dt), so the limb arrives without
    //    overshoot, stays while the hand stays and returns without bouncing. The target never depends on the bone's own state (no
    //    feedback: the old force-to-spring loop was a Newton's cradle between the hand and the hull it pushed). Elbow and knee are hinges:
    //    only flexion is theirs, the rest swings the shoulder/hip. Reactions' flinch (Kick) is a velocity impulse into the same filter.
    //
    // Her segment names read from HerBody (HerBody.SegNames): upperarm_L/R, forearm_L/R (arms), thigh_L/R, calf_L/R, head, neck, chest, spine.
    // Contact fades (0.15 s): arms by TouchingHer("upperarm_X"/"forearm_X"); legs by Soft.Touching("thighOut_X"/"thighIn_X"/"glute_X");
    // head by TouchingHer("head"/"neck"); spine by TouchingHer("spine"/"chest") or Soft.Touching("belly") (small addition to the spec,
    // so a held head or torso is not wobbled under the hand). 0.8.0: a group whose segment yields (pen > 5 mm) fades too.
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

        // pendulum ripple (design 13.2): cap of a joint's linear acceleration (m/s^2) and of the rod's angular acceleration (rad/s^2), gain
        const float AccMax = 30f, PendMax = 150f, PendGain = 1.0f;
        // the yield filter is exact for any dt, but a frame longer than this is clamped to it (a hitch is not a cut)
        const float MaxFilterDt = 0.1f;

        // planted lock v2 (design 13.1): still / swinging speed (m/s) and how long each is held, ease up / down (s)
        const float PlantDist = 0.08f, StillSpeed = 0.15f, SwingSpeed = 0.5f, StillHold = 0.08f, SwingHold = 0.10f, PlantUp = 0.15f, PlantDown = 0.30f;
        const float YieldDown = 0.10f;              // the pin lets go faster than a free swing: the yield attacks at 4-5 Hz

        // limb yield (design-0.8.0 section 3). Per region {arms, legs, head, spine}: the gain G (metres the contact point moves per metre of
        // penetration, at LimbPush 0.5), the soft cap Dmax of that displacement (metres) and the highest flinch angular velocity (rad/s)
        static readonly float[] YieldGainR = { 1.0f, 0.5f, 0.6f, 0.35f };
        static readonly float[] YieldDmaxR = { 0.15f, 0.08f, 0.06f, 0.04f };
        static readonly float[] KickVmaxR = { 10f, 4f, 6f, 2f };
        const float MinLever = 0.03f;                                  // m: a contact closer than this to the joint means no rotation
        const float RestAngle = 0.0005f, RestVel = 0.01f;              // rad, rad/s: below both (and no target) the filter state is dropped
        const float ReleaseK = 0.7f;                                   // the return is slower than the give: f_release = 0.7 f_attack
        const float YieldTouchPen = 0.005f;                            // m: a segment pressed this deep fades the lag term of its group
        const float LogMinDeg = 1f, SettleMinDeg = 0.5f;               // first-yield log threshold; the least peak that counts as 'settled'

        // Per-bone yield constants: clamp (deg), attack frequency (Hz), inertia (kg m^2). Matched by prefix of the bone's name.
        struct YSpec { public string Bone; public float ClampDeg, F, I; public YSpec(string b, float c, float f, float i) { Bone = b; ClampDeg = c; F = f; I = i; } }
        static readonly YSpec[] YSpecs =
        {
            new YSpec("Shoulder_", 35f, 2.5f, 0.25f), new YSpec("Elbow_", 40f, 5.0f, 0.05f), new YSpec("Hip_", 20f, 2.0f, 0.60f), new YSpec("Knee_", 25f, 4.0f, 0.15f),
            new YSpec("Neck_M", 12f, 2.5f, 0.05f), new YSpec("Head_M", 15f, 4.0f, 0.03f), new YSpec("Spine1_M", 8f, 1.5f, 0.80f), new YSpec("Chest_M", 8f, 2.0f, 0.60f),
        };

        // Segment -> bone DISPLACEMENT shares (bone names are Overlap bones, segments HerBody.SegNames): the share of the contact point's
        // displacement this bone provides by turning about its origin. pelvis, butt_L/R move nothing: Root_M is never moved.
        struct YieldMap { public string Seg, Bone; public float Share; public YieldMap(string s, string b, float f) { Seg = s; Bone = b; Share = f; } }
        static readonly YieldMap[] YieldMaps =
        {
            new YieldMap("upperarm_L", "Shoulder_L", 1f), new YieldMap("forearm_L", "Elbow_L", 0.65f), new YieldMap("forearm_L", "Shoulder_L", 0.35f),
            new YieldMap("upperarm_R", "Shoulder_R", 1f), new YieldMap("forearm_R", "Elbow_R", 0.65f), new YieldMap("forearm_R", "Shoulder_R", 0.35f),
            new YieldMap("thigh_L", "Hip_L", 1f), new YieldMap("calf_L", "Knee_L", 0.6f), new YieldMap("calf_L", "Hip_L", 0.4f),
            new YieldMap("thigh_R", "Hip_R", 1f), new YieldMap("calf_R", "Knee_R", 0.6f), new YieldMap("calf_R", "Hip_R", 0.4f),
            new YieldMap("head", "Head_M", 0.6f), new YieldMap("head", "Neck_M", 0.4f), new YieldMap("neck", "Neck_M", 1f),
            new YieldMap("chest", "Chest_M", 0.6f), new YieldMap("chest", "Spine1_M", 0.4f), new YieldMap("spine", "Spine1_M", 1f),
            new YieldMap("breast_L", "Chest_M", 0.5f), new YieldMap("breast_L", "Spine1_M", 0.25f),
            new YieldMap("breast_R", "Chest_M", 0.5f), new YieldMap("breast_R", "Spine1_M", 0.25f),
        };
        struct YieldRes { public int Seg; public B Bone; public float Share; }

        // groups: 0 arm L, 1 arm R, 2 leg L, 3 leg R, 4 head, 5 spine; region (weight slot): arms 0, legs 1, head 2, spine 3
        static readonly string[] GroupLabel = { "arm L", "arm R", "thigh L", "thigh R", "head", "spine" };
        static readonly int[] GroupRegion = { 0, 0, 1, 1, 2, 3 };
        // the HerBody segments of each group (a yield on any of them fades the group's lag term)
        static readonly string[][] GroupSegs =
        {
            new[] { "upperarm_L", "forearm_L" }, new[] { "upperarm_R", "forearm_R" }, new[] { "thigh_L", "calf_L" }, new[] { "thigh_R", "calf_R" },
            new[] { "head", "neck" }, new[] { "spine", "chest", "breast_L", "breast_R" },
        };
        // per region {arms, legs, head, spine}
        static readonly float[] ClampDegR = { 15f, 12f, 20f, 8f };
        static readonly float[] RippleGainR = { 1.5f, 0.8f, 1.6f, 0.6f };
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

        // the pendulum's rod: each bone hangs from its pivot towards the next chain bone; the leaves (hand, foot, head) toward a fixed length
        // away from the previous one
        static readonly string[] ChainFrom = { "Shoulder_L", "Elbow_L", "Hip_L", "Knee_L", "Shoulder_R", "Elbow_R", "Hip_R", "Knee_R", "Spine1_M", "Chest_M", "Neck_M" };
        static readonly string[] ChainTo = { "Elbow_L", "Wrist_L", "Knee_L", "Ankle_L", "Elbow_R", "Wrist_R", "Knee_R", "Ankle_R", "Chest_M", "Neck_M", "Head_M" };
        static readonly string[] LeafBone = { "Wrist_L", "Wrist_R", "Ankle_L", "Ankle_R", "Head_M" };
        static readonly string[] LeafAnchor = { "Elbow_L", "Elbow_R", "Knee_L", "Knee_R", "Neck_M" };
        static readonly float[] LeafLen = { 0.10f, 0.10f, 0.10f, 0.10f, 0.12f };

        class B
        {
            public Transform T, P; public int Group; public float F, K, C;
            public Quaternion Target, Prev, Qs, LastWrite; public Vector3 Omega, LastPos;
            public bool Init, HasLast;
            // ripple: parent's world rotation last frame and its angular velocity; PState 0 none, 1 rotation known, 2 rotation and velocity known
            public Quaternion PW = Quaternion.identity; public Vector3 POmega; public int PState;
            // pendulum ripple: the joint's world velocity last frame (HasV: valid), the rod below it (Child) or the bone it hangs from (Anchor, a leaf)
            public Vector3 LastV; public bool HasV; public B Child, Anchor; public float LeafL;
            // 0.8.0 limb yield (every bone but the wrists and ankles). Yv: the yield rotation vector (rad, in the parent's frame) and Yvel its
            // velocity (rad/s): the filter state. Yt: this frame's target vector, rebuilt every frame from the segments. YieldLive: the state is
            // non-zero or a target/kick is pending (the bone is awake). KickW: a pending angular impulse (N*m*s, world) from Kick.
            public bool CanYield, YieldLive, Unwound, YLogged;
            public Vector3 Yv, Yvel, Yt, KickW;
            public float YClamp, YF, YI;                   // clamp (rad), attack frequency (Hz), inertia (kg m^2)
            public Quaternion PRot = Quaternion.identity, PInv = Quaternion.identity;   // the parent's rotation in the animation's pose, and its inverse (this frame)
            // hinges (Elbow_X, Knee_X): the chain's proximal / distal bones, the bend-plane normal (world and in the parent's frame) this frame
            public bool Hinge, HingeOk; public B Prox, Dist; public Vector3 HingeH, HingeL;
            // diagnostics: the strongest cause this frame, the peak since the last rest and over the last 10 s (deg)
            public float YPen, YPeak, YMaxDeg10; public int YSeg; public string YBy;
        }

        class Eff
        {
            public int Group; public B A, M, E; public bool On;
            public Vector3 AnimPos, AnimPrev; public Quaternion AnimRot;
            public bool HasPrev, AnimOk;
            public float StillT, FreeT, Planted = 1f, Target = 1f, Speed;   // Target 1 = pinned, 0 = free; Planted eases to it
            public int DistalSeg = -1;                                        // forearm_X / calf_X of this chain (HerBody.SegNames index): a push on it releases the pin
            public int RefIdx;                                                // the frame that gave the least speed (RefName)
            public readonly Vector3[] RefPrev = new Vector3[5];               // the effector's animation position in each reference frame's space, last frame
            public readonly bool[] RefOk = new bool[5];
            public float LogAt, PlantSum; public int PlantN;                  // log at most every 10 s; the 10 s average of Planted
            public int NearIdx = -1; public Transform NearT; public Vector3 NearLoc; public bool NearHas; public string NearName;
        }

        readonly List<B> _bones = new List<B>(16);
        readonly Eff[] _effs = new Eff[4];
        Transform _root;
        Character _her;
        readonly float[] _contact = new float[6];
        readonly bool[] _touched = new bool[6];
        // planted lock reference frames: 0 world (null), 1 her pelvis, 2..4 partner rig roots
        readonly Transform[] _frames = new Transform[5];
        readonly string[] _frameName = new string[5];
        readonly Transform[] _newRoots = new Transform[3];
        int _frameVer = -1, _frameCount = -1, _unwoundFrame = -1; object _frameList;
        bool _loggedRipple;
        float _corrSum, _corrMax, _rkSum, _rkMax; int _corrN, _rkN; string _rkBone;
        readonly float[] _region = new float[4];
        readonly float[] _zeta = new float[4];
        readonly float[] _fs = new float[4];
        readonly bool[] _loggedFade = new bool[6];
        readonly bool[] _loggedReset = new bool[3];
        readonly bool[] _loggedHinge = new bool[2];       // elbow, knee
        float _str = 1f;                                  // 0.8.0 Strength (1..4)
        float _ripple, _lp;
        uint _yieldBits;                                  // bit per HerBody segment that yields this frame (HerBody.YieldOn)
        readonly int[] _yieldCm = new int[HerBody.SegNames.Length];   // that segment's penetration in whole cm (status text only)
        int _sigY;
        YieldRes[] _yieldRes = new YieldRes[0];           // YieldMaps resolved to bones at build (missing bones skipped)
        readonly int[][] _grpSegs = new int[6][];         // GroupSegs resolved to HerBody segment indices
        bool _loggedDriven;
        long _sig = -1;
        readonly StringBuilder _sb = new StringBuilder(160);
        // 10 s diagnostics
        float _logAt; int _settled, _kicks; float _kickMax; string _kickBone;

        void Build(Character her)
        {
            _bones.Clear();
            for (int i = 0; i < _effs.Length; i++) _effs[i] = null;
            _root = her.Root; _her = her;
            for (int i = 0; i < _contact.Length; i++) _contact[i] = 0f;
            _loggedDriven = false; _sig = -1; _sigY = 0; _frameVer = -1; _unwoundFrame = -1; _loggedRipple = false;
            for (int k = 0; k < _frames.Length; k++) { _frames[k] = null; _frameName[k] = null; }
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
                var b = new B { T = t, P = t.parent, Group = sp.Group, F = sp.F };
                for (int k = 0; k < YSpecs.Length; k++)
                {
                    if (!sp.Name.StartsWith(YSpecs[k].Bone, StringComparison.Ordinal)) continue;
                    b.CanYield = true; b.YClamp = YSpecs[k].ClampDeg * Mathf.Deg2Rad; b.YF = YSpecs[k].F; b.YI = YSpecs[k].I;
                    break;
                }
                b.Hinge = sp.Name.StartsWith("Elbow_", StringComparison.Ordinal) || sp.Name.StartsWith("Knee_", StringComparison.Ordinal);
                _bones.Add(b);
            }
            if (missing.Length > 0) Plugin.Log.LogInfo("Overlap: bones not found, skipped: " + missing);
            // hinge chains: Shoulder/Elbow/Wrist and Hip/Knee/Ankle (the proximal bone takes what a hinge cannot do)
            for (int i = 0; i < _bones.Count; i++)
            {
                var b = _bones[i];
                if (!b.Hinge) continue;
                string n = b.T.name, side = n.Substring(n.Length - 2);
                bool elbow = n.StartsWith("Elbow_", StringComparison.Ordinal);
                b.Prox = FindB((elbow ? "Shoulder" : "Hip") + side);
                b.Dist = FindB((elbow ? "Wrist" : "Ankle") + side);
            }
            for (int i = 0; i < ChainFrom.Length; i++)
            {
                B from = FindB(ChainFrom[i]), to = FindB(ChainTo[i]);
                if (from != null && to != null) from.Child = to;
            }
            for (int i = 0; i < LeafBone.Length; i++)
            {
                B leaf = FindB(LeafBone[i]), anchor = FindB(LeafAnchor[i]);
                if (leaf != null && anchor != null) { leaf.Anchor = anchor; leaf.LeafL = LeafLen[i]; }
            }
            Transform pelvis;
            if (!her.Bones.TryGetValue("Root_M", out pelvis) || pelvis == null)
            {
                pelvis = null;
                if (her.Root != null) foreach (var x in her.Root.GetComponentsInChildren<Transform>(true)) if (x.name == "Root_M") { pelvis = x; break; }
            }
            _frames[1] = pelvis; _frameName[1] = "her pelvis";
            var res = new List<YieldRes>(YieldMaps.Length);
            for (int i = 0; i < YieldMaps.Length; i++)
            {
                int si = Array.IndexOf(HerBody.SegNames, YieldMaps[i].Seg);
                B yb = FindB(YieldMaps[i].Bone);
                if (si >= 0 && yb != null && yb.CanYield) res.Add(new YieldRes { Seg = si, Bone = yb, Share = YieldMaps[i].Share });
            }
            _yieldRes = res.ToArray();
            for (int g = 0; g < _grpSegs.Length; g++)
            {
                var names = GroupSegs[g];
                var idx = new List<int>(names.Length);
                for (int k = 0; k < names.Length; k++) { int si = Array.IndexOf(HerBody.SegNames, names[k]); if (si >= 0) idx.Add(si); }
                _grpSegs[g] = idx.ToArray();
            }
            for (int e = 0; e < _effs.Length; e++)
            {
                var n = EffNames[e];
                B a = FindB(n[0]), m = FindB(n[1]), d = FindB(n[2]);
                if (a != null && m != null && d != null)
                {
                    _effs[e] = new Eff { Group = e, A = a, M = m, E = d };
                    string side = n[2].Substring(n[2].Length - 2);   // "_L" / "_R" from Wrist_L / Ankle_L
                    _effs[e].DistalSeg = Array.IndexOf(HerBody.SegNames, (e < 2 ? "forearm" : "calf") + side);
                }
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
            if (_unwoundFrame != Time.frameCount) Unwind();    // HerBody's block in Body did not run this frame (physics and hands off): do it here

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
            _str = float.IsNaN(s.Strength) ? 1f : Mathf.Clamp(s.Strength, 1f, 4f);
            _ripple = Mathf.Clamp01(s.Ripple);
            _lp = Mathf.Clamp01(s.LimbPush);
            _yieldBits = 0u;

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
            int n = dt > 0f ? Mathf.Min(12, Mathf.Max(1, Mathf.CeilToInt(dt / Step - 1e-3f))) : 0;
            float h = n > 0 ? dt / n : 0f;

            // The animation's end-effector poses, BEFORE any write this frame (the chain is still the animation's, with Body's scales),
            // and the planted detection on them.
            CollectFrames(s.Caps);
            for (int e = 0; e < _effs.Length; e++)
            {
                var ef = _effs[e];
                if (ef == null) continue;
                Detect(ef, s, dt, reset);
            }

            // Limb yield targets, from HerBody.YieldOn (measured against the animation's pose before any write below). Independent of the
            // overlap amount: with Overlap 0 a pressed limb still yields and comes back.
            PrepareYield();
            if (_lp > 0f && HerBody.Built) YieldPass();

            int driven = 0, active = 0;
            for (int i = 0; i < _bones.Count; i++)
            {
                var b = _bones[i];
                var tr = b.T;
                int rg = GroupRegion[b.Group];
                float wReq = _region[rg];
                Quaternion cur = tr.localRotation;
                bool unwound = b.Unwound; b.Unwound = false;     // Unwind put the animation's rotation back on this bone this frame
                // A bone with a yield in progress (or a target / kick this frame) keeps running at region weight 0: the lag term stays off (w = 0
                // below, the written rotation is the animation's target) and only the yield rotation is added on top. With no yield and no lag
                // the bone is left alone (exactly the game's animation). Not gated on LimbPush: when LimbPush goes to 0 or the source
                // disappears (hand lifted, physics hands off) the offset must decay through the filter instead of snapping to the animation.
                bool yieldWake = wReq <= 0f && b.CanYield && (b.YieldLive || b.Yt.sqrMagnitude > 0f || (_lp > 0f && b.KickW.sqrMagnitude > 1e-12f));
                if (wReq <= 0f && !yieldWake)
                {
                    // Off for this bone: if the animation did not rewrite it, put the animation's rotation back, once.
                    if (b.HasLast && Same(cur, b.LastWrite)) tr.localRotation = b.Target;
                    b.HasLast = false; b.Init = false; b.PState = 0;
                    b.Yv = b.Yvel = Vector3.zero; b.YieldLive = false; b.KickW = Vector3.zero; b.HasV = false;
                    continue;
                }
                active++;

                // Target: the animation's rotation, unless our last write is still there (the animation didn't write the bone).
                bool animWrote = !unwound && !(b.HasLast && Same(cur, b.LastWrite));
                Vector3 pos = tr.position;
                bool cut = false;         // a real cut (the bone jumped or turned): only this drops the yield state and a pending kick; a long frame does not
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
                        b.Init = false; cut = why > 1;
                    }
                }
                bool cont = b.Init;       // the bone was processed last frame too: LastPos (and LastV) are its previous pose
                if (!b.Init)
                {
                    if (animWrote) b.Target = cur;
                    b.Prev = b.Target; b.Qs = b.Target; b.Omega = Vector3.zero; b.Init = true;
                    // every reset path zeroes the ripple state; the yield state only on a cut (design 2 of the follow-up: a hitch is not a cut)
                    b.PState = 0; b.POmega = Vector3.zero; b.HasV = false;
                    if (cut) { b.Yv = b.Yvel = Vector3.zero; b.YieldLive = false; b.YPeak = 0f; b.KickW = Vector3.zero; }
                }
                // the joint's world acceleration (pendulum ripple); needs two consecutive normal frames
                Vector3 accel = Vector3.zero; bool haveAcc = false;
                if (cont && dt > 0f && !reset)
                {
                    Vector3 vj = (pos - b.LastPos) / dt;
                    if (b.HasV) { accel = (vj - b.LastV) / dt; haveAcc = Ok(accel); }
                    b.LastV = vj; b.HasV = Ok(vj);
                }
                else if (dt > 0f) b.HasV = false;
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

                // Pendulum ripple (design 13.2): the joint's linear acceleration swings the limb hanging from it like a uniform rod hinged at the
                // joint (it lags behind the lurch): alpha = -(3 / 2L) u x A, A the pivot's acceleration, u the rod's direction, L its length.
                // Independent of what the parent's weight is: it only needs the joint's own motion. The state's velocity is the limb's
                // angular velocity in the parent's frame, so the world alpha is added.
                if (wReq > 0f && haveAcc && n > 0 && _ripple > 0f)
                {
                    float a2 = accel.sqrMagnitude;
                    if (a2 > AccMax * AccMax) accel *= AccMax / Mathf.Sqrt(a2);
                    Vector3 u = Vector3.zero; float rod = 0f;
                    if (b.Child != null && b.Child.T != null) { Vector3 dd = b.Child.T.position - pos; rod = dd.magnitude; if (rod > 0.02f) u = dd / rod; }
                    else if (b.Anchor != null && b.Anchor.T != null) { Vector3 dd = pos - b.Anchor.T.position; float dm = dd.magnitude; if (dm > 0.02f) { u = dd / dm; rod = b.LeafL; } }
                    if (rod > 0.02f && u.sqrMagnitude > 0.5f)
                    {
                        Vector3 alphaP = Vector3.Cross(u, accel) * (-1.5f / rod);
                        float am = alphaP.magnitude;
                        if (am > PendMax) { alphaP *= PendMax / am; am = PendMax; }
                        if (Ok(alphaP))
                        {
                            Quaternion pwq = b.P != null ? b.P.rotation : Quaternion.identity;
                            b.Omega += (Quaternion.Inverse(pwq) * alphaP) * (_ripple * RippleGainR[rg] * PendGain * dt);
                            _rkSum += am; _rkN++;
                            if (am > _rkMax) { _rkMax = am; _rkBone = tr.name; }
                            if (!_loggedRipple) { _loggedRipple = true; Plugin.Log.LogInfo("Overlap: ripple v2: angular + pendulum kicks from the parent's motion (PendGain " + PendGain.ToString("0.0") + ")"); }
                        }
                    }
                }

                // Flinch (Reactions): a pending angular impulse J (N*m*s, world) is an instant change of the filter's velocity, d(v) = J / I
                // (in the parent's frame, the animation pose's), capped per region so no kick outruns the clamp; the filter then returns it
                // without overshoot. Wakes a sleeping bone. A paused frame (n 0) keeps the kick for the next one.
                if (b.CanYield && n > 0 && _lp > 0f && b.KickW.sqrMagnitude > 1e-12f)
                {
                    Vector3 dv = (b.PInv * b.KickW) / b.YI;
                    float vm = dv.magnitude, vmax = KickVmaxR[rg];
                    if (vm > vmax) { dv *= vmax / vm; vm = vmax; }
                    if (Ok(dv) && vm > 1e-5f)
                    {
                        b.Yvel += dv; b.YieldLive = true;
                        _kicks++;
                        if (vm > _kickMax) { _kickMax = vm; _kickBone = tr.name; }
                    }
                }
                if (n > 0 || !b.CanYield || !(_lp > 0f)) b.KickW = Vector3.zero;

                // The yield filter, once per frame (exact for any dt).
                if (b.CanYield && n > 0)
                {
                    if (b.Yt.sqrMagnitude > 0f) b.YieldLive = true;
                    if (b.YieldLive) YieldStep(b, Mathf.Min(dt, MaxFilterDt));
                }

                // Substeps with the target interpolated from last frame's to this frame's. (Nothing to lag at region weight 0: the spring
                // just stays on the target.)
                if (wReq > 0f)
                {
                    float clampDot = ClampDotR[rg], clampDeg = ClampDegR[rg];
                    for (int k = 1; k <= n; k++)
                    {
                        Quaternion tk = n == 1 ? b.Target : Quaternion.Slerp(b.Prev, b.Target, (float)k / n);
                        SpringStep(ref b.Qs, ref b.Omega, tk, b.K, b.C, h);
                        // Lag clamp on the state itself so it cannot wind up beyond it.
                        float dot = Mathf.Abs(Quaternion.Dot(tk, b.Qs));
                        if (dot < clampDot) b.Qs = Quaternion.RotateTowards(tk, b.Qs, clampDeg);
                    }
                    if (float.IsNaN(b.Qs.x) || float.IsNaN(b.Qs.w) || float.IsNaN(b.Omega.x)) { b.Qs = b.Target; b.Omega = Vector3.zero; }
                }
                else { b.Qs = b.Target; b.Omega = Vector3.zero; }
                if (float.IsNaN(b.PW.x) || float.IsNaN(b.POmega.x)) { b.PW = Quaternion.identity; b.POmega = Vector3.zero; b.PState = 0; }

                // Contact fades only the lag term; the yield (if any) stays on top of it.
                // Strength (1..4) scales the shown lag beyond the state (SlerpUnclamped), never the state itself, so the springs stay as stable
                // as at 1x; the shown lag is capped at Strength x the region's lag clamp and never more than 50 deg.
                float w = wReq * (1f - _contact[b.Group]) * _str;
                Quaternion o;
                if (w <= 0f) o = b.Target;
                else if (w <= 1f) o = w >= 0.9999f ? b.Qs : Quaternion.Slerp(b.Target, b.Qs, w);
                else
                {
                    o = Quaternion.SlerpUnclamped(b.Target, b.Qs, w);
                    float cap = Mathf.Min(50f, ClampDegR[rg] * _str);
                    if (Quaternion.Angle(b.Target, o) > cap) o = Quaternion.RotateTowards(b.Target, o, cap);
                    if (float.IsNaN(o.x) || float.IsNaN(o.w)) o = b.Qs;
                }
                if (b.YieldLive)
                {
                    float ya = b.Yv.magnitude;
                    if (ya > 1e-6f) o = Quaternion.AngleAxis(ya * Mathf.Rad2Deg, b.Yv / ya) * o;   // premultiplied: the vector is in the parent's frame
                }
                if (!Same(o, cur)) tr.localRotation = o;
                b.LastWrite = o; b.HasLast = true;
                if (wReq > 0f) driven++;
            }

            // Planted correction, after all springs and the yield (the chains' lag shows as the elbow/knee moving while the hand/foot stays).
            ApplyPlanted();

            if (active > 0 && !_loggedDriven)
            {
                _loggedDriven = true;
                _sb.Length = 0;
                _sb.Append("Overlap: yield v3 (position-based, critically damped: ");
                for (int k = 0; k < 4; k++) _sb.Append(k > 0 ? " / " : "").Append(YSpecs[k].Bone.TrimEnd('_')).Append(' ').Append(YSpecs[k].F.ToString("0.#")).Append(" Hz");
                _sb.Append(") driving ").Append(active).Append(" bones: ");
                bool firstB = true;
                for (int i = 0; i < _bones.Count; i++)
                {
                    var bb = _bones[i];
                    if (!(_region[GroupRegion[bb.Group]] > 0f) && !bb.CanYield) continue;
                    _sb.Append(firstB ? "" : ", ").Append(bb.T.name); firstB = false;
                }
                Plugin.Log.LogInfo(_sb.ToString());
            }
            SetStatus(driven);
            LogYield();
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

        // ---- limb yield ----

        // Per frame, before any write: zero the targets, remember each bone's parent rotation in the animation's pose (every conversion of
        // the frame uses it: the parents are written one by one below) and the bend-plane normal of each hinge from the animation's chain.
        void PrepareYield()
        {
            for (int i = 0; i < _bones.Count; i++)
            {
                var b = _bones[i];
                b.Yt = Vector3.zero; b.YPen = 0f; b.YSeg = -1; b.YBy = null;
                b.PRot = b.P != null ? b.P.rotation : Quaternion.identity;
                b.PInv = Quaternion.Inverse(b.PRot);
                if (!b.Hinge) continue;
                b.HingeOk = false;
                if (b.Prox == null || b.Dist == null || b.Prox.T == null || b.Dist.T == null) continue;
                Vector3 jp = b.T.position, v1 = b.Prox.T.position - jp, v2 = b.Dist.T.position - jp;
                Vector3 c = Vector3.Cross(v1, v2);
                float cm = c.magnitude, l = v1.magnitude * v2.magnitude;
                if (!(cm > 0.1f * l) || !(l > 1e-8f) || !Ok(c)) continue;      // a straight limb (within ~6 deg): the hinge axis is unknown
                // h = toParent x toChild: rotating the child about +h EXTENDS the joint (elbow at the origin, shoulder at -x, wrist at
                // (+x,-y)/sqrt2 gives h = +z, and +z turns the forearm toward +x, away from the upper arm), so a push may only turn it about -h
                b.HingeH = c / cm; b.HingeL = b.PInv * b.HingeH; b.HingeOk = true;
            }
        }

        // The target rotation vector of every bone from the segments' yield: the contact point wants to move by d (m, along the push
        // direction, soft-capped), each bone provides its share by turning about its origin.
        void YieldPass()
        {
            float gk = _lp / 0.5f;
            for (int i = 0; i < _yieldRes.Length; i++)
            {
                var r = _yieldRes[i];
                Vector3 pt, nrm; float pen; string by; int hand;
                if (!HerBody.YieldOn(r.Seg, out pt, out nrm, out pen, out by, out hand) || !Ok(pt) || !Ok(nrm) || float.IsNaN(pen)) continue;
                if ((_yieldBits & (1u << r.Seg)) == 0u) { _yieldBits |= 1u << r.Seg; _yieldCm[r.Seg] = Mathf.RoundToInt(pen * 100f); }
                int rg = GroupRegion[r.Bone.Group];
                float dmax = YieldDmaxR[rg];
                float d = dmax * (float)Math.Tanh(YieldGainR[rg] * gk * pen / dmax);   // linear for a brush, never reaches the cap
                AddYield(r.Bone, pt, nrm * (d * r.Share), pen, r.Seg, by);
            }
            for (int i = 0; i < _bones.Count; i++)
            {
                var b = _bones[i];
                float m = b.Yt.magnitude;
                if (!Ok(b.Yt)) b.Yt = Vector3.zero;
                else if (b.CanYield && m > b.YClamp) b.Yt *= b.YClamp / m;
                if (!b.YLogged && b.YSeg >= 0 && b.Yt.magnitude * Mathf.Rad2Deg >= LogMinDeg)
                {
                    b.YLogged = true;
                    Plugin.Log.LogInfo("Overlap: first yield on " + b.T.name + ": " + (b.Yt.magnitude * Mathf.Rad2Deg).ToString("0") + " deg (" + HerBody.SegNames[b.YSeg]
                        + " by " + (b.YBy ?? "a hand") + ", " + (b.YPen * 100f).ToString("0.0") + " cm deep)");
                }
            }
        }

        // Bone b turns about its origin so that the point `pt` moves by d (only the part of d perpendicular to the lever arm is a rotation).
        // Ball joints take any axis; a hinge keeps only the flexion part and gives the rest to the proximal bone of its chain.
        void AddYield(B b, Vector3 pt, Vector3 d, float pen, int seg, string by)
        {
            if (b.T == null || !Ok(d)) return;
            Vector3 r = pt - b.T.position;
            float rm = r.magnitude;
            if (!(rm >= MinLever)) return;                                // too close to the joint to mean a rotation
            Vector3 rh = r / rm;
            Vector3 dp = d - rh * Vector3.Dot(d, rh);
            if (!(dp.magnitude >= 1e-5f)) return;
            // rotation vector (world, rad) that carries pt along dp: axis = r x dp / |r x dp|, angle = |dp| / |r|; |r x dp| = |r||dp| since they are perpendicular
            Vector3 v = Vector3.Cross(r, dp) / (rm * rm);
            if (!Ok(v)) return;
            if (pen > b.YPen) { b.YPen = pen; b.YSeg = seg; b.YBy = by; }
            if (!b.Hinge) { AddRot(b, v); return; }
            Vector3 rest = v;
            if (b.HingeOk)
            {
                float vh = Vector3.Dot(v, b.HingeH);
                if (vh < 0f) { Vector3 flex = b.HingeH * vh; AddRot(b, flex); rest = v - flex; }   // flexion stays at the hinge
            }
            if (b.Prox != null && rest.magnitude > 1e-6f)
            {
                if (rest.magnitude > 0.035f)
                {
                    int ix = b.T.name.StartsWith("Elbow_", StringComparison.Ordinal) ? 0 : 1;
                    if (!_loggedHinge[ix])
                    {
                        _loggedHinge[ix] = true;
                        Plugin.Log.LogInfo("Overlap: " + (ix == 0 ? "elbow" : "knee") + " hinge: flexion-only yield, out-of-plane pushes swing the " + (ix == 0 ? "shoulder" : "hip")
                            + (b.HingeOk ? "" : " (limb straight: everything)"));
                    }
                }
                // what this joint cannot do, as the displacement it would have made at the point, for the proximal bone
                AddYield(b.Prox, pt, Vector3.Cross(rest, r), pen, seg, by);
            }
        }

        static void AddRot(B b, Vector3 vWorld) { b.Yt += b.PInv * vWorld; }

        // The critically damped filter toward Yt (design 3.6): x'' + 2w x' + w^2 (x - xt) = 0 solved exactly over dt with xt constant,
        // per component of the rotation vector. Faster when moving away from rest than back to it. Clamped, no wind-up against the clamp.
        void YieldStep(B b, float dt)
        {
            float f = b.Yt.sqrMagnitude > b.Yv.sqrMagnitude ? b.YF : b.YF * ReleaseK;
            float w = 2f * Mathf.PI * f;
            Vector3 e = b.Yv - b.Yt;
            Vector3 c2 = b.Yvel + e * w;
            float ex = Mathf.Exp(-w * dt);
            b.Yv = b.Yt + (e + c2 * dt) * ex;
            b.Yvel = (b.Yvel - c2 * (w * dt)) * ex;
            float m = b.Yv.magnitude;
            if (m > b.YClamp)
            {
                Vector3 u = b.Yv / m;
                b.Yv = u * b.YClamp;
                float vd = Vector3.Dot(b.Yvel, u);
                if (vd > 0f) b.Yvel -= u * vd;
            }
            if (b.Hinge && b.HingeOk)
            {
                // a critically damped filter can still cross the target once when it carries velocity: never into hyperextension
                float x = Vector3.Dot(b.Yv, b.HingeL);
                if (x > 0f) b.Yv -= b.HingeL * x;
                float vx = Vector3.Dot(b.Yvel, b.HingeL);
                if (vx > 0f && x > 0f) b.Yvel -= b.HingeL * vx;
            }
            if (!Ok(b.Yv) || !Ok(b.Yvel)) { b.Yv = b.Yvel = Vector3.zero; b.YieldLive = false; return; }
            float deg = b.Yv.magnitude * Mathf.Rad2Deg;
            if (deg > b.YMaxDeg10) b.YMaxDeg10 = deg;
            if (deg > b.YPeak) b.YPeak = deg;
            if (b.Yt.sqrMagnitude <= 0f && b.Yv.magnitude < RestAngle && b.Yvel.magnitude < RestVel)
            {
                if (b.YPeak >= SettleMinDeg) _settled++;
                b.YPeak = 0f;
                b.Yv = b.Yvel = Vector3.zero; b.YieldLive = false;
            }
        }

        // A group counts as touched while one of its segments yields (pen > 5 mm), so the lag term does not wobble a limb that is being held.
        bool YieldTouch(int g)
        {
            if (!(_lp > 0f) || !HerBody.Built) return false;
            var idx = _grpSegs[g];
            for (int i = 0; i < idx.Length; i++)
            {
                Vector3 pt, nrm; float pen; string by; int hand;
                if (HerBody.YieldOn(idx[i], out pt, out nrm, out pen, out by, out hand) && pen > YieldTouchPen) return true;
            }
            return false;
        }

        // ---- planted lock ----

        static void ResetEff(Eff e)
        {
            e.Planted = 1f; e.Target = 1f; e.StillT = 0f; e.FreeT = 0f; e.HasPrev = false;      // default pinned: the lock can only help
            for (int f = 0; f < e.RefOk.Length; f++) e.RefOk[f] = false;
            e.NearIdx = -1; e.NearT = null; e.NearHas = false;
        }

        // The frames an effector's motion is measured in (design 13.1): 0 the world, 1 her pelvis (Root_M), 2..4 the partner rigs' roots
        // (from the capsules' sources; re-collected when the capsule set changes). A hand resting on a thrusting partner is still in HIS frame.
        void CollectFrames(List<SoftCapsule> caps)
        {
            if (VrHands.Version == _frameVer && ReferenceEquals(caps, _frameList) && (caps == null ? 0 : caps.Count) == _frameCount) return;
            _frameVer = VrHands.Version; _frameList = caps; _frameCount = caps == null ? 0 : caps.Count;
            int found = 0;
            var roots = _newRoots;
            roots[0] = roots[1] = roots[2] = null;
            if (caps != null)
                for (int i = 0; i < caps.Count && found < 3; i++)
                {
                    var c = caps[i];
                    if (c.T == null || c.Src == null || c.Src.Kind != "partner") continue;
                    Transform r = c.T.root;
                    if (r == null || ReferenceEquals(r, _root) || (_root != null && _root.IsChildOf(r))) continue;
                    bool dup = false;
                    for (int k = 0; k < found; k++) if (ReferenceEquals(roots[k], r)) { dup = true; break; }
                    if (!dup) roots[found++] = r;
                }
            for (int k = 0; k < 3; k++)
            {
                if (ReferenceEquals(_frames[2 + k], roots[k])) continue;
                _frames[2 + k] = roots[k];
                _frameName[2 + k] = roots[k] != null ? roots[k].name : null;
                for (int e = 0; e < _effs.Length; e++) if (_effs[e] != null) _effs[e].RefOk[2 + k] = false;   // a different frame: no motion known in it yet
            }
        }

        // Planted lock v2 (design 13.1). Records the animation's pose of the end effector (after Unwind, before any write of ours) and how
        // still it is: the least speed over every reference frame (and the nearest capsule's). Still (< 0.15 m/s for 0.08 s) = pinned to the
        // animation; swinging (> 0.5 m/s for 0.10 s) = free; in between the state is kept (a slow caress stays pinned, a fast swing trails).
        void Detect(Eff e, OverlapSettings s, float dt, bool reset)
        {
            e.AnimOk = false;
            e.On = (s.PlantedLock || s.YieldLock) && e.A.T != null && e.M.T != null && e.E.T != null;
            if (!e.On) { ResetEff(e); return; }
            Vector3 p = e.E.T.position;
            if (!Ok(p)) { ResetEff(e); return; }
            e.AnimPrev = e.AnimPos; e.AnimPos = p; e.AnimRot = e.E.T.rotation;
            e.AnimOk = true;
            bool prevOk = e.HasPrev;
            if (prevOk && (e.AnimPos - e.AnimPrev).sqrMagnitude > ResetPos * ResetPos) { ResetEff(e); e.HasPrev = true; StoreRefs(e, p, -1f); return; }   // a cut
            e.HasPrev = true;
            if (reset || !(dt > 0f)) { StoreRefs(e, p, -1f); return; }       // a hitch or a pause: keep the state, no speed from this frame

            float best = float.MaxValue; int bi = -1;
            StoreRefs(e, p, dt, ref best, ref bi);
            float rel = float.MaxValue;
            if (FindNear(e, s.Caps, p))
            {
                if (e.NearHas) rel = (p - e.NearT.TransformPoint(e.NearLoc)).magnitude / dt;   // motion relative to the capsule's frame
                e.NearLoc = e.NearT.InverseTransformPoint(p); e.NearHas = true;
            }
            if (rel < best) { best = rel; bi = 5; }
            e.Speed = best; e.RefIdx = bi;
            float newT = e.Target;
            if (best < StillSpeed) { e.StillT += dt; e.FreeT = 0f; if (e.StillT >= StillHold) newT = 1f; }
            else if (best < float.MaxValue && best > SwingSpeed) { e.FreeT += dt; e.StillT = 0f; if (e.FreeT >= SwingHold) newT = 0f; }
            else { e.StillT = 0f; e.FreeT = 0f; }
            // 0.8.0: a push on the chain's distal segment (forearm / calf) must move the hand / foot: the pin lets go while that segment yields or
            // the elbow / knee still carries yield (a kick too), or the IK would put the hand back and undo the yield. An upper-arm / thigh push
            // keeps the pin: the elbow / knee bends, the hand / foot stays. Open loop: the yield never reads the pin, so this cannot oscillate.
            bool distal = false;
            if (_lp > 0f && HerBody.Built && e.DistalSeg >= 0)
            {
                Vector3 yp, yn; float ypen; string yby; int yh;
                distal = HerBody.YieldOn(e.DistalSeg, out yp, out yn, out ypen, out yby, out yh) && ypen > YieldTouchPen;
            }
            if (!distal && e.M.CanYield && e.M.YieldLive && e.M.Yv.magnitude > 0.035f) distal = true;
            if (!s.PlantedLock) newT = 0f;   // on only for YieldLock: no stillness pin
            if (s.YieldLock)
            {
                // the hand / foot stays at the animation's pose while any joint of its chain yields; the IK bends the elbow / knee instead
                bool chain = distal || (e.A.CanYield && e.A.YieldLive && e.A.Yv.magnitude > 0.035f);
                if (chain) { newT = 1f; e.StillT = 0f; e.FreeT = 0f; distal = false; }
            }
            else if (distal) { newT = 0f; e.StillT = 0f; e.FreeT = 0f; }
            if (newT != e.Target)
            {
                e.Target = newT;
                float now = Time.unscaledTime;
                if (now >= e.LogAt)
                {
                    e.LogAt = now + 10f;
                    if (newT > 0f)
                        Plugin.Log.LogInfo("Overlap: " + EffLabel[e.Group] + " planted (still relative to " + RefName(e, bi) + ", " + best.ToString("0.00") + " m/s)");
                    else
                        Plugin.Log.LogInfo("Overlap: " + EffLabel[e.Group] + " free (" + (distal ? "the " + (e.Group < 2 ? "forearm" : "calf") + " yields" : "swinging " + (best < 1e5f ? best : 0f).ToString("0.0") + " m/s") + ")");
                }
            }
            e.Planted = Mathf.MoveTowards(e.Planted, e.Target, dt / (e.Target > e.Planted ? PlantUp : (distal ? YieldDown : PlantDown)));
            e.PlantSum += e.Planted; e.PlantN++;
        }

        string RefName(Eff e, int bi)
        {
            if (bi == 0) return "the world";
            if (bi == 1) return "her pelvis";
            if (bi >= 2 && bi <= 4) return _frameName[bi] ?? "a partner";
            return e.NearName ?? "a capsule";
        }

        void StoreRefs(Eff e, Vector3 p, float dt) { float b = 0f; int i = 0; StoreRefs(e, p, dt, ref b, ref i); }

        // Speed of p in each frame against last frame's position in it (dt > 0), then remember p in each frame. Keeps the least in best / bi.
        void StoreRefs(Eff e, Vector3 p, float dt, ref float best, ref int bi)
        {
            for (int f = 0; f < e.RefOk.Length; f++)
            {
                Transform fr = f == 0 ? null : _frames[f];
                if (f > 0 && fr == null) { e.RefOk[f] = false; continue; }
                if (dt > 0f && e.RefOk[f])
                {
                    Vector3 was = f == 0 ? e.RefPrev[0] : fr.TransformPoint(e.RefPrev[f]);
                    float sp = (p - was).magnitude / dt;
                    if (sp < best) { best = sp; bi = f; }
                }
                e.RefPrev[f] = f == 0 ? p : fr.InverseTransformPoint(p);
                e.RefOk[f] = true;
            }
        }

        // After all our writes: every effector our writes moved off the animation's pose (the chain's lag, the yield, a kick, the torso's
        // lag carried down) goes back by a two-bone IK, by its Planted weight. Feet keep half the ankle pin (a stiff foot reads wrong).
        void ApplyPlanted()
        {
            for (int e = 0; e < _effs.Length; e++)
            {
                var ef = _effs[e];
                if (ef == null || !ef.On || !ef.AnimOk || !(ef.Planted > 0.001f)) continue;
                Vector3 off = ef.E.T.position - ef.AnimPos;
                if (off.sqrMagnitude < 0.0005f * 0.0005f && Quaternion.Angle(ef.E.T.rotation, ef.AnimRot) < 0.1f) continue;   // nothing of ours moved it
                Quaternion qa = ef.A.T.localRotation, qm = ef.M.T.localRotation, qe = ef.E.T.localRotation;
                if (!Ik.TwoBone(ef.A.T, ef.M.T, ef.E.T, ef.AnimPos, ef.AnimRot, ef.Planted, ef.Group >= 2 ? 0.5f : 1f)) continue;
                Pin(ef.A, qa); Pin(ef.M, qm); Pin(ef.E, qe);
                float c = off.magnitude;
                if (Ok(off)) { _corrSum += c; _corrN++; if (c > _corrMax) _corrMax = c; }
            }
        }

        // The IK's rotation is what the bone holds now (the next frame's animation-wrote test compares against it). A bone that was asleep has
        // no Target yet: the rotation it held before the IK is the animation's of this frame, so Unwind / the next wake can give it back.
        static void Pin(B b, Quaternion before)
        {
            Quaternion now = b.T.localRotation;
            if (Same(now, before)) return;
            if (!b.HasLast) { b.Target = before; b.HasLast = true; }
            b.LastWrite = now;
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

        static bool Ok(Vector3 v) { return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)); }

        bool Touch(Character her, int g)
        {
            if (YieldTouch(g)) return true;
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
            int sigY = 17;
            for (int sg = 0; sg < _yieldCm.Length; sg++) if ((_yieldBits & (1u << sg)) != 0u) sigY = sigY * 31 + _yieldCm[sg] + 1;
            uint lo = (uint)driven | ((uint)Mathf.RoundToInt(_region[0] * 10f) << 8) | ((uint)Mathf.RoundToInt(_region[1] * 10f) << 12) | ((uint)Mathf.RoundToInt(_region[2] * 10f) << 16)
                    | ((uint)Mathf.RoundToInt(_region[3] * 10f) << 20) | ((uint)fade << 24);
            long sig = (long)lo | ((long)planted << 32) | ((long)Mathf.RoundToInt(_ripple * 10f) << 36) | ((long)_yieldBits << 41);
            if (sig == _sig && sigY == _sigY) return;
            _sig = sig; _sigY = sigY;
            if (driven == 0)
            {
                Status = _bones.Count == 0 ? "off: no chain bones found" : "off (Amount 0)";
                if (_yieldBits != 0u)
                {
                    _sb.Length = 0; _sb.Append(Status);
                    AppendYielding(_sb);
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
            AppendYielding(_sb);
            first = true;
            for (int g = 0; g < 6; g++)
                if ((fade & (1 << g)) != 0) { _sb.Append(first ? "; faded: " : ", ").Append(GroupLabel[g]).Append(" contact"); first = false; }
            Status = _sb.ToString();
        }

        // "; yielding: forearm_L (3 cm), upperarm_L (2 cm)" (whole cm: the string is rebuilt only when one changes)
        void AppendYielding(StringBuilder sb)
        {
            bool first = true;
            for (int sg = 0; sg < _yieldCm.Length; sg++)
                if ((_yieldBits & (1u << sg)) != 0u) { sb.Append(first ? "; yielding: " : ", ").Append(HerBody.SegNames[sg]).Append(" (").Append(_yieldCm[sg]).Append(" cm)"); first = false; }
        }

        // Every 10 s while anything yielded: the peak angles, how often a bone came to rest, the flinch kicks.
        void LogYield()
        {
            float now = Time.unscaledTime;
            if (_logAt <= 0f) { _logAt = now + 10f; return; }
            if (now < _logAt) return;
            _logAt = now + 10f;
            StringBuilder sb = null;
            for (int i = 0; i < _bones.Count; i++)
            {
                var b = _bones[i];
                if (b.YMaxDeg10 >= SettleMinDeg)
                {
                    if (sb == null) sb = new StringBuilder("Overlap: last 10 s yield: "); else sb.Append(", ");
                    sb.Append(b.T.name).Append(" max ").Append(b.YMaxDeg10.ToString("0")).Append(" deg");
                }
                b.YMaxDeg10 = 0f;
            }
            if (sb != null || _kicks > 0)
            {
                if (sb == null) sb = new StringBuilder("Overlap: last 10 s yield: none");
                sb.Append("; settled ").Append(_settled).Append(_settled == 1 ? " time" : " times");
                sb.Append("; kicks ").Append(_kicks);
                if (_kicks > 0) sb.Append(" (max ").Append(_kickMax.ToString("0.0")).Append(" rad/s on ").Append(_kickBone).Append(")");
                Plugin.Log.LogInfo(sb.ToString());
            }
            _settled = 0; _kicks = 0; _kickMax = 0f; _kickBone = null;
            // planted lock: how long each effector was pinned, how far the IK had to move it back (what our writes would have slid it)
            StringBuilder sp = null;
            for (int e = 0; e < _effs.Length; e++)
            {
                var ef = _effs[e];
                if (ef == null) continue;
                if (ef.PlantN > 0)
                {
                    if (sp == null) sp = new StringBuilder("Overlap: last 10 s planted: "); else sp.Append(", ");
                    sp.Append(EffShort[e]).Append(' ').Append(Mathf.RoundToInt(ef.PlantSum / ef.PlantN * 100f)).Append(" %");
                }
                ef.PlantSum = 0f; ef.PlantN = 0;
            }
            if (sp != null)
            {
                if (_corrN > 0) sp.Append("; corrections avg ").Append((_corrSum / _corrN * 100f).ToString("0.0")).Append(" cm max ").Append((_corrMax * 100f).ToString("0.0")).Append(" cm");
                else sp.Append("; no corrections");
                Plugin.Log.LogInfo(sp.ToString());
            }
            _corrSum = 0f; _corrMax = 0f; _corrN = 0;
            if (_rkN > 0)
                Plugin.Log.LogInfo("Overlap: last 10 s ripple kicks (pendulum) avg " + (_rkSum / _rkN).ToString("0") + " rad/s^2 max " + _rkMax.ToString("0") + " on " + _rkBone);
            _rkSum = 0f; _rkMax = 0f; _rkN = 0; _rkBone = null;
        }

        // 0.8.0 (design-0.8.0 section 1): puts the animation's rotation back on bones the animation did not rewrite this frame, so
        // HerBody's yield query (which runs next, before Update) sees the animation's pose, not last frame's yield. Update's animWrote test
        // treats these bones as not animated (b.Unwound). Costs nothing on a fully animated rig; otherwise one write per such bone.
        public void Unwind()
        {
            _unwoundFrame = Time.frameCount;
            for (int i = 0; i < _bones.Count; i++)
            {
                var b = _bones[i];
                b.Unwound = false;
                if (b.T == null || !b.HasLast) continue;
                if (!Same(b.T.localRotation, b.LastWrite)) continue;     // the animation rewrote it: it already holds the animation's rotation
                b.T.localRotation = b.Target;
                b.Unwound = true;
            }
        }

        // package C (0.7.9 reactions): a linear impulse (N*s, world) on her segment `segIndex` (HerBody.SegNames) at `point` (world), as
        // an angular impulse on every bone that carries the segment (YieldMaps), applied at the next Update.
        // Units: impulse N*s at lever arm r (m) = angular impulse J = share * cross(point - bone, impulse) in N*m*s; the filter turns it
        // into a velocity change d(v) = J / I (rad/s, I per bone in YSpecs, capped per region by KickVmaxR). Several kicks in a frame
        // add up. A hinge keeps the flexing part only (like the yield itself; the shoulder/hip has its own share). A no-op when LimbPush is 0.
        public void Kick(int segIndex, Vector3 point, Vector3 impulseNs)
        {
            if (!(_lp > 0f) || !Ok(point) || !Ok(impulseNs)) return;
            for (int i = 0; i < _yieldRes.Length; i++)
            {
                var r = _yieldRes[i];
                if (r.Seg != segIndex || r.Bone.T == null) continue;
                Vector3 j = Vector3.Cross(point - r.Bone.T.position, impulseNs) * r.Share;
                if (!Ok(j)) continue;
                var b = r.Bone;
                if (b.Hinge)
                {
                    if (!b.HingeOk) continue;
                    float jh = Vector3.Dot(j, b.HingeH);
                    if (jh >= 0f) continue;
                    j = b.HingeH * jh;
                }
                b.KickW += j;
            }
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
            _root = null; _her = null; _sig = -1; _sigY = 0; _yieldBits = 0u;
            _yieldRes = new YieldRes[0];
            Status = "";
        }
    }
}
