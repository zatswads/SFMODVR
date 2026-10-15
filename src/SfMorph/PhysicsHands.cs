using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace SfMorph
{
    // Settings of the physics hands (the integrator fills them from the config every frame).
    internal class HandsSettings
    {
        public bool On = false, Collide = true, HideVisual = false, ShowColliders = false;
        public int Mesh = 0;           // 0 the game's own hands (extracted from the player's body), 1 simple primitives
        public float Softness = 0.5f;  // 0..1, how deep her soft zones may yield (also how much her colliders are inset)
        // 0.7.8 (plumbed from the config, used by the hull / friction / finger packages): convex-hull body colliders instead of capsules,
        // how much her skin grips the hands (0..1), simulated finger bodies on joints
        public bool HullColliders = true, JointedFingers = true;
        public float Friction = 0.5f;
        // 0.8.0: the drawn hand is the controller in free air (offset faded in on contact), fingers conform to her skin, the rest curl
        public bool DrawnOffsetFade = true, FingerConform = true;
        public float RestCurl = 0.25f;
        // Controller -> wrist (m, degrees), in the controller's axes, shared with Embody: the position is where the wrist sits
        // relative to the tracked pose; the rotation is added on top of an automatic alignment (fingers along the controller's
        // forward, thumb along its up, palm toward the body's midline), so zero is the natural grip.
        public Vector3 LeftPos = new Vector3(-0.0325f, -0.0379f, -0.1012f), LeftRot = Vector3.zero;
        public Vector3 RightPos = new Vector3(0.0325f, -0.0379f, -0.1012f), RightRot = Vector3.zero;
    }

    // Collision events only reach scripts on the rigidbody's object, so this tiny component (on the palm and on every finger
    // rigidbody) forwards them: first contacts for the log and the touch times behind PhysicsHands.TouchingHer.
    internal class PhysicsHandProbe : MonoBehaviour
    {
        public bool Left;
        void OnCollisionEnter(Collision c) { PhysicsHands.NoteContact(Left, c, true); }
        void OnCollisionStay(Collision c) { PhysicsHands.NoteContact(Left, c, false); }
    }

    // The user's VR hands as physics objects: a rigidbody palm with finger capsules that follows the controller by velocity,
    // collides with a kinematic capsule model of her body (private layers, nothing else in the scene feels them) and shows a
    // hand mesh that stays 1:1 with the controller in free air and shows the blocked displacement when pushing into her.
    // Her soft zones read the visual hand's capsules (Capsules). Nothing here runs per frame except cheap pose writes.
    internal static partial class PhysicsHands
    {
        public static bool Active;                                  // hands are built: the integrator uses Capsules instead of VrHands' "vr" ones
        public static int Version;                                  // bumped whenever Capsules changes
        public static string Status = "";
        public static Transform LeftWrist, RightWrist;
        // 0.7.2: the simulated fingers' local rotations (5 fingers x 3 bones: thumb, index, middle, ring, pinky x proximal, middle, distal;
        // each in its bone's parent frame) and the same bones' rest local rotations, so Embody can apply delta = Inverse(rest) * current
        // to his own finger bones. Valid (this frame) only while the hand is built and tracked; a missing bone reads identity/identity.
        public static readonly Quaternion[] LeftFingerLocal = new Quaternion[15], RightFingerLocal = new Quaternion[15];
        public static readonly Quaternion[] LeftFingerRest = new Quaternion[15], RightFingerRest = new Quaternion[15];
        public static bool LeftFingersValid, RightFingersValid;
        // The signed curl (degrees, + = toward the palm) of each of those bones: the twist of Inverse(rest) * current about its curl axis.
        public static readonly float[] LeftFingerCurlDeg = new float[15], RightFingerCurlDeg = new float[15];
        // TouchingHer(name) lives in PhysicsHands.Rig.cs.
        // The visual wrists (Embody's IK targets when it follows the hands).
        // Per hand, this frame: the displacement the DRAWN hand gets from the controller target. 0.8.0: the SHOWN offset, gated: zero in
        // free air, the blocked body's displacement faded in on contact (UpdateHand). Zero / identity when the hand is missing, kinematic
        // (Collide off) or not built. Embody composes it into its wrist targets, so his hand and the drawn hand agree.
        public static Vector3 LeftDp, RightDp;
        public static Quaternion LeftDq = Quaternion.identity, RightDq = Quaternion.identity;
        public static readonly List<SoftCapsule> Capsules = new List<SoftCapsule>();

        // 0.8.0 (design-0.8.0 section 5.6): this hand's capsules at the CONTROLLER target pose (no blocked-body offset, no lead), for her
        // yield: her limb is pushed from where the hand is, not from where the physics hand got blocked. The endpoints are the drawn hand's
        // capsules (the visual fingers' pose) re-expressed in the palm frame at the controller pose. Called by HerBody.Update, BEFORE
        // PhysicsHands.Update in the frame: the controller is fresh, the finger pose at most one frame old. Writes at most `max` entries
        // from `start`; returns the count; 0 when not Active or no controller. Hands with collisions off still publish (their "vr"
        // capsules are Owner "PhysicsHands" and the contact categoriser drops them: this ghost is then the only source for the hand).
        internal static int FillGhost(WorldCap[] into, int start, int max)
        {
            if (into == null || max <= 0 || start < 0 || !Active || _s == null) return 0;
            int n = 0;
            for (int side = 0; side < 2; side++)
            {
                var h = side == 0 ? _left : _right;
                if (h == null || h.VGo == null || h.VRoot == null || h.VBones == null || h.Src == null || h.GhostN == 0) continue;
                var ctl = side == 0 ? VrHands.LeftController : VrHands.RightController;
                if (ctl == null) continue;
                Vector3 tp; Quaternion tq;
                Target(ctl, h.Left ? _s.LeftPos : _s.RightPos, h.Left ? _s.LeftRot : _s.RightRot, h.Spec.Align, out tp, out tq);
                if (!Finite(tp) || !Finite(tq)) continue;
                float u = h.Spec.Scale;
                var vr = h.VRoot;
                for (int i = 0; i < h.GhostN && n < max && start + n < into.Length; i++)
                {
                    var bone = h.VBones[h.GhostBone[i]];
                    if (bone == null) continue;
                    // InverseTransformPoint divides by the root's scale (root units): back to metres in the palm frame
                    Vector3 la = vr.InverseTransformPoint(bone.TransformPoint(h.GhostA[i])) * u;
                    Vector3 lb = vr.InverseTransformPoint(bone.TransformPoint(h.GhostB[i])) * u;
                    var wc = new WorldCap { A = tp + tq * la, B = tp + tq * lb, R = h.GhostR[i], Hand = side, By = h.Src.Name, Src = h.Src };
                    if (!Finite(wc.A) || !Finite(wc.B)) continue;
                    into[start + n] = wc;
                    n++;
                }
            }
            return n;
        }

        // -1 when the collider is not one of her segment colliders
        internal static int SegIndexOf(Collider c)
        {
            SegBody b;
            return !ReferenceEquals(c, null) && c != null && _segOfCol.TryGetValue(c, out b) && b.S != null ? b.S.Index : -1;
        }
        static readonly Func<Collider, int> _segOfFn = SegIndexOf;   // cached: a method group converts to a new delegate on every use

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const string Pfx = "SfPhys";
        const string HerPfx = "SfPhysHer_";
        const float PalmMass = 0.35f;
        // 0.7.8 follow fix (see StepHand / UpdateHand): the palm chases the frame-sampled controller target plus the velocity it had (feed
        // forward) and closes the remaining gap at GapGain per second; the sample is extrapolated by at most MaxLead seconds to the fixed
        // step. MaxFf caps the sampled speed, MaxGapV the speed the gap term may add. A target that moves more than JumpBase + JumpSpeed *
        // frame time in one frame is the game moving the rig the controllers hang under (a POV cut), not the hand: the hand is carried along.
        const float GapGain = 25f, MaxLead = 0.06f, MaxFf = 4f, MaxGapV = 3f, JumpBase = 0.12f, JumpSpeed = 8f;
        // 0.7.9 velocity-level follow: the fraction of the velocity error removed per step (< 1 so it can never overshoot into a
        // step-to-step oscillation), the rotation gap gain (1/s) and its cap (rad/s), and the torque cap (N*m) turned into a per-step
        // angular velocity cap through the hand's inertia
        const float FollowGain = 0.7f, TurnGain = 0.6f, RotGapGain = 25f, MaxGapW = 10f, TorqueCap = 4f;
        // 0.8.0: while the hand touches her the torque cap drops (N*m): the palm flattens on her instead of holding the controller's exact
        // roll; the difference shows as the drawn rotation offset, which is the contact feel
        const float TouchTorqueCap = 1.5f;
        // 0.8.0 drawn-offset gate (section 4): the raw offset below the first number shows as nothing, above the second as itself
        const float GateMmLo = 0.005f, GateMmHi = 0.015f, GateDegLo = 4f, GateDegHi = 10f, GateAttack = 0.04f, GateRelease = 0.25f;
        const float HerMaxV = 4f;                  // sampled speed cap of her kinematic segments (m/s)
        const int CookPerFrame = 2;                // convex hulls cooked per rendered frame (the capsule collides until then)

        // Sizes (metres, from the design).
        // 0.7.7: the soft capsules of the palm are half of the 3 cm physical palm (PalmThick), not the old 3.5 cm radius that stuck 2 cm
        // out of the hand mesh on both faces; three of them (wrist -> index / middle / pinky root) cover the 8 cm width without a valley.
        const float PalmThick = 0.015f, FingerR = 0.011f, ThumbR = 0.0125f, FingerVisR = 0.0095f;
        static readonly int[] PalmFingers = { 1, 2, 4 };
        static readonly float[] FingerMax = { 80f, 90f, 60f };     // curl at full grip, proximal / middle / distal
        static readonly float[] ThumbMax = { 20f, 35f, 45f };

        // ---- game types (Assembly-CSharp isn't referenced) ----

        static bool _typesLooked;
        static Type _handType, _moveType;
        internal static Type HandType { get { Types(); return _handType; } }

        static void Types()
        {
            if (_typesLooked) return;
            _typesLooked = true;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != "Assembly-CSharp") continue;
                _handType = _handType ?? a.GetType("VR_HandController");
                _moveType = _moveType ?? a.GetType("MovePlayerAndGirl");
            }
            Plugin.Log.LogInfo("PhysicsHands: " + (_handType != null ? "VR_HandController" : "no VR_HandController (controller models stay, grip from XR only)")
                               + (_moveType != null ? ", MovePlayerAndGirl" : ", no MovePlayerAndGirl (primitive hands)"));
        }

        internal static object Get(object o, string name)
        {
            if (o == null) return null;
            var uo = o as UnityEngine.Object;
            if (!ReferenceEquals(uo, null) && uo == null) return null;   // destroyed
            var t = o.GetType();
            var f = t.GetField(name, Any);
            if (f != null) return f.GetValue(o);
            var p = t.GetProperty(name, Any);
            return p != null && p.GetIndexParameters().Length == 0 ? p.GetValue(o, null) : null;
        }

        static bool IsUi(Transform t)
        {
            for (var p = t; p != null; p = p.parent) if (p is RectTransform) return true;   // a Canvas always sits on a RectTransform
            return false;
        }

        static bool Finite(Vector3 v) { return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)); }
        static bool Finite(Quaternion q) { return !(float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w) || float.IsInfinity(q.x) || float.IsInfinity(q.y) || float.IsInfinity(q.z) || float.IsInfinity(q.w)); }

        static readonly HashSet<string> _once = new HashSet<string>();
        static void LogOnce(string key, string msg) { if (_once.Add(key)) Plugin.Log.LogInfo("PhysicsHands: " + msg); }

        static void SetStatus(string s)
        {
            if (Status == s) return;
            Status = s;
            Plugin.Log.LogInfo("PhysicsHands: " + (s.Length == 0 ? "(status cleared)" : s));
        }

        // ---- the game objects we look up now and then (never per frame) ----

        static Component _moveObj, _hcObj;
        static float _moveNext, _hcNext;

        static GameObject FindPlayer()
        {
            Types();
            if (_moveType == null) return null;
            if (_moveObj == null)
            {
                if (Time.unscaledTime < _moveNext) return null;
                _moveNext = Time.unscaledTime + 2f;
                _moveObj = UnityEngine.Object.FindObjectOfType(_moveType) as Component;
                if (_moveObj == null) return null;
            }
            return Get(_moveObj, "player") as GameObject;
        }

        internal static Component HandController()
        {
            Types();
            if (_handType == null) return null;
            if (_hcObj == null)
            {
                if (Time.unscaledTime < _hcNext) return null;
                _hcNext = Time.unscaledTime + 2f;
                _hcObj = UnityEngine.Object.FindObjectOfType(_handType) as Component;
            }
            return _hcObj;
        }

        // ================= geometry specs =================

        sealed class BoneSpec
        {
            public string Name;
            public int Parent = -1;                       // index into Bones, -1: child of the hand root
            public Vector3 Pos, Scale = Vector3.one;      // local to the parent
            public Quaternion Rot = Quaternion.identity;
            public Matrix4x4 M = Matrix4x4.identity;      // hand-root space (the wrist's own space), root units
        }

        sealed class ShapeSpec                            // a collider or a primitive of the visual
        {
            public int Bone;
            public bool Box;
            public Vector3 A, B;                          // capsule ends in root units (box: both its centre)
            public Vector3 Size;                          // box size (metres): x across the palm, y thick, z along the fingers
            public Quaternion Rot = Quaternion.identity;  // box rotation in root space
            public float R;                               // capsule radius (metres)
            public string Tag;
        }

        sealed class CapSpec { public int Bone; public Vector3 A, B; public float R; }

        sealed class Section { public Mesh Mesh; public Material[] Mats; public string From; }

        sealed class HandSpec
        {
            public string Visual = "primitives";
            public readonly List<BoneSpec> Bones = new List<BoneSpec>();
            public readonly int[] Fin = { -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 };   // [finger*3 + joint], finger 0 = thumb
            public float Scale = 1f;                       // metres per root unit
            public Quaternion Align = Quaternion.identity; // wrist rotation = controller * offset * Align
            public readonly Vector3[] FAxis = new Vector3[5];   // curl axis per finger, root space
            public readonly Vector3[] FDir = new Vector3[5];    // finger direction (first joint -> last), root space
            public readonly List<ShapeSpec> Cols = new List<ShapeSpec>(), Prims = new List<ShapeSpec>();
            public readonly List<CapSpec> Caps = new List<CapSpec>();
            public readonly List<Section> Sections = new List<Section>();
            public string Note = "";
        }

        static int AddBone(HandSpec sp, string name, int parent, Vector3 posRoot)
        {
            var b = new BoneSpec { Name = name, Parent = parent, M = Matrix4x4.TRS(posRoot, Quaternion.identity, Vector3.one) };
            b.Pos = parent >= 0 ? posRoot - (Vector3)sp.Bones[parent].M.GetColumn(3) : posRoot;
            sp.Bones.Add(b);
            return sp.Bones.Count - 1;
        }

        // A hand with no game rig behind it: five fingers of three bones in a flat open pose, fingers along local +-X (the way a
        // wrist bone of a mirrored rig tends to point), thumb on +Z, palm toward -Y. The alignment makes the frame irrelevant.
        static HandSpec SyntheticSpec(bool left)
        {
            var sp = new HandSpec { Visual = "primitives" };
            float sx = left ? -1f : 1f;
            Vector3 fwd = new Vector3(sx, 0f, 0f), side = new Vector3(0f, 0f, 1f), nrm = new Vector3(0f, -1f, 0f);
            AddBone(sp, "Wrist", -1, Vector3.zero);
            string[] names = { "Thumb", "Index", "Middle", "Ring", "Pinky" };
            float[] across = { 0f, 0.030f, 0.010f, -0.011f, -0.031f };
            float[][] len = { new[] { 0.045f, 0.032f, 0.026f }, new[] { 0.040f, 0.024f, 0.020f }, new[] { 0.044f, 0.027f, 0.021f }, new[] { 0.041f, 0.025f, 0.019f }, new[] { 0.032f, 0.019f, 0.017f } };
            for (int f = 0; f < 5; f++)
            {
                Vector3 p, dir;
                if (f == 0) { p = fwd * 0.025f + side * 0.030f + nrm * 0.008f; dir = (fwd * 0.55f + side * 0.75f + nrm * 0.35f).normalized; }
                else { p = fwd * 0.088f + side * across[f]; dir = fwd; }
                int parent = 0;
                for (int j = 0; j < 3; j++)
                {
                    int bi = AddBone(sp, names[f] + (j + 1), parent, p);
                    sp.Fin[f * 3 + j] = bi;
                    parent = bi;
                    p += dir * len[f][j];
                }
            }
            string why = FinishSpec(sp, true, nrm);
            if (why != null) throw new Exception("synthetic hand: " + why);
            sp.Note = "synthetic";
            return sp;
        }

        // From the bones' root-space poses and the finger map: the palm frame, the alignment to the controller, the curl axes,
        // the colliders (palm box + 2 capsules per finger), the visual primitives and the soft-zone capsules.
        // Returns null when fine, else why not. Scale (metres per root unit) must be set already.
        static string FinishSpec(HandSpec sp, bool knownNormal, Vector3 normalV)
        {
            float u = sp.Scale;
            var P = new Vector3[5][];
            var cnt = new int[5];
            for (int f = 0; f < 5; f++)
            {
                P[f] = new Vector3[3];
                for (int j = 0; j < 3; j++)
                {
                    int bi = sp.Fin[f * 3 + j];
                    if (bi < 0) break;
                    P[f][j] = sp.Bones[bi].M.GetColumn(3);
                    cnt[f]++;
                }
            }
            Vector3 kn = Vector3.zero;
            int nk = 0, first = -1, last = -1;
            for (int f = 1; f < 5; f++) if (cnt[f] >= 1) { kn += P[f][0]; nk++; if (first < 0) first = f; last = f; }
            if (nk < 2) return "fewer than two finger chains";
            kn /= nk;
            if (kn.magnitude * u < 0.03f) return "palm too short (" + (kn.magnitude * u).ToString("0.000") + " m)";
            Vector3 fwd = kn.normalized;
            Vector3 across = P[first][0] - P[last][0];                  // pinky -> index: the thumb side
            across -= fwd * Vector3.Dot(across, fwd);
            if (across.magnitude * u < 0.01f) return "palm too narrow";
            across.Normalize();
            Vector3 n0 = Vector3.Cross(fwd, across).normalized;
            float width = (P[first][0] - P[last][0]).magnitude * u;

            // which way is the palm: knownNormal, else the fingers' rest curvature, else the thumb's side of the palm plane
            Vector3 normal; string how;
            if (knownNormal) { normal = normalV.normalized; how = "known"; }
            else
            {
                float sum = 0f; int nf = 0;
                for (int f = 1; f < 5; f++)
                {
                    if (cnt[f] < 3) continue;
                    var d = (P[f][1] - P[f][0]).normalized;
                    var v = P[f][2] - P[f][0];
                    v -= d * Vector3.Dot(v, d);
                    sum += Vector3.Dot(v, n0); nf++;
                }
                float mm = nf > 0 ? sum / nf * u * 1000f : 0f;
                float sign = 1f;
                if (Mathf.Abs(mm) >= 1f) { sign = mm > 0f ? 1f : -1f; how = "finger curvature " + mm.ToString("0.0") + " mm"; }
                else if (cnt[0] >= 2 && Mathf.Abs(Vector3.Dot(P[0][cnt[0] - 1], n0)) * u >= 0.003f)
                {
                    float t = Vector3.Dot(P[0][cnt[0] - 1], n0);
                    sign = t > 0f ? 1f : -1f; how = "thumb side " + (t * u * 1000f).ToString("0") + " mm";
                }
                else how = "assumed (open flat hand)";
                normal = n0 * sign;
            }
            sp.Note += (sp.Note.Length > 0 ? ", " : "") + "palm side by " + how;

            // Alignment (same construction as Embody's calibration): z along the fingers, y toward the thumb (pinky root -> thumb
            // root, squared to z); the wrist's rotation for a wanted frame F is F * Inverse(LookRotation(z, y)).
            Vector3 raw = across;
            if (cnt[0] >= 1)
            {
                var pk = P[last][0];
                var th = P[0][0] - pk;
                if (th.magnitude * u > 0.02f) raw = th;
            }
            raw -= fwd * Vector3.Dot(raw, fwd);
            if (raw.sqrMagnitude < 1e-10f) raw = across;
            raw.Normalize();
            sp.Align = Quaternion.Inverse(Quaternion.LookRotation(fwd, raw));

            // curl axes: rotating about cross(finger direction, palm normal) moves the tip toward the palm
            for (int f = 0; f < 5; f++)
            {
                Vector3 dir = cnt[f] >= 2 ? (P[f][cnt[f] - 1] - P[f][0]).normalized : fwd;
                Vector3 ax = Vector3.Cross(dir, normal);
                if (ax.sqrMagnitude < 1e-8f) ax = Vector3.Cross(fwd, normal);
                sp.FAxis[f] = ax.normalized;
                sp.FDir[f] = dir;
            }

            // colliders: palm box + 2 capsules per finger (proximal; middle + distal to the tip)
            Vector3 centre = kn * 0.5f;
            Quaternion boxRot = Quaternion.LookRotation(fwd, normal);
            sp.Cols.Add(new ShapeSpec { Bone = 0, Box = true, A = centre, B = centre, Rot = boxRot, Size = new Vector3(width + 0.025f, 0.030f, kn.magnitude * u + 0.012f), Tag = "palm" });
            sp.Prims.Add(new ShapeSpec { Bone = 0, Box = true, A = centre, B = centre, Rot = boxRot, Size = new Vector3(width + 0.012f, 0.026f, kn.magnitude * u + 0.006f), Tag = "palm" });
            int palmCaps = 0;
            foreach (int pf in PalmFingers)
                if (cnt[pf] >= 1) { sp.Caps.Add(new CapSpec { Bone = 0, A = P[pf][0] * 0.1f, B = P[pf][0], R = PalmThick }); palmCaps++; }
            if (palmCaps == 0) sp.Caps.Add(new CapSpec { Bone = 0, A = kn * 0.15f, B = kn * 0.9f, R = PalmThick });   // only the ring finger was found
            string[] fn = { "thumb", "index", "middle", "ring", "pinky" };
            for (int f = 0; f < 5; f++)
            {
                int n = cnt[f];
                if (n < 1) continue;
                float r = f == 0 ? ThumbR : FingerR;
                Vector3 tip = n >= 2 ? P[f][n - 1] + (P[f][n - 1] - P[f][n - 2]) * 0.8f : P[f][0] + fwd * (0.02f / u);
                for (int j = 0; j < n; j++)
                {
                    Vector3 a = P[f][j], b = j + 1 < n ? P[f][j + 1] : tip;
                    int bi = sp.Fin[f * 3 + j];
                    sp.Prims.Add(new ShapeSpec { Bone = bi, A = a, B = b, R = f == 0 ? 0.011f : FingerVisR, Tag = fn[f] + (j + 1) });
                    sp.Caps.Add(new CapSpec { Bone = bi, A = a, B = b, R = FingerR });
                }
                if (n >= 2)
                {
                    sp.Cols.Add(new ShapeSpec { Bone = sp.Fin[f * 3], A = P[f][0], B = P[f][1], R = r, Tag = fn[f] + "A" });
                    sp.Cols.Add(new ShapeSpec { Bone = sp.Fin[f * 3 + 1], A = P[f][1], B = tip, R = r, Tag = fn[f] + "B" });
                }
                else sp.Cols.Add(new ShapeSpec { Bone = sp.Fin[f * 3], A = P[f][0], B = tip, R = r, Tag = fn[f] });
            }
            return null;
        }

        // ================= the game's hand: extracted from the player's body mesh =================

        static int FingerOf(string raw)
        {
            var n = Body.Norm(raw);
            if (n.Contains("thumb")) return 0;
            if (n.Contains("index")) return 1;
            if (n.Contains("middle")) return 2;
            if (n.Contains("pinky") || n.Contains("little")) return 4;
            // "ring" is also inside spring/string/earring: only as a finger name
            if (n.Contains("ringfinger") || n.StartsWith("ring", StringComparison.Ordinal) || (n.Contains("ring") && n.Contains("finger"))) return 3;
            return -1;
        }

        static void Decompose(Matrix4x4 m, out Vector3 pos, out Quaternion rot, out Vector3 scale)
        {
            pos = m.GetColumn(3);
            Vector3 c0 = m.GetColumn(0), c1 = m.GetColumn(1), c2 = m.GetColumn(2);
            scale = new Vector3(c0.magnitude, c1.magnitude, c2.magnitude);
            rot = c1.sqrMagnitude > 1e-12f && c2.sqrMagnitude > 1e-12f ? Quaternion.LookRotation(c2 / scale.z, c1 / scale.y) : Quaternion.identity;
        }

        // The player's hand skeleton + mesh for one side, or null with why. Kept bones K = Wrist_X's subtree + Wrist_X's parent
        // (driven rigid with the wrist); every K bone is posed in the wrist's space from the bind poses, Wrist.bind * bone.bind^-1.
        static HandSpec GameSpec(bool left, out string why)
        {
            why = null;
            var player = FindPlayer();
            if (player == null) { why = "no player in the scene"; return null; }
            var all = player.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            string wn = left ? "wristl" : "wristr";
            Transform wrist = null;
            foreach (var r in all)
            {
                if (r == null || r.sharedMesh == null || r.bones == null) continue;
                foreach (var b in r.bones) if (b != null && Body.Norm(b.name) == wn) { wrist = b; break; }
                if (wrist != null) break;
            }
            if (wrist == null) { why = "no Wrist_" + (left ? "L" : "R") + " among the player's skinned bones"; return null; }
            var cands = new List<SkinnedMeshRenderer>();
            foreach (var r in all)
                if (r != null && r.sharedMesh != null && r.bones != null && Array.IndexOf(r.bones, wrist) >= 0) cands.Add(r);
            var bodies = new List<SkinnedMeshRenderer>();
            foreach (var r in cands)
                if (r.name.ToLowerInvariant().Contains("body") || r.sharedMesh.name.ToLowerInvariant().Contains("body")) bodies.Add(r);
            var srcs = bodies.Count > 0 ? bodies : cands;

            // reference skin: the one holding the most K bones
            Transform forearmT = wrist.parent;
            SkinnedMeshRenderer refR = null; int most = -1;
            foreach (var r in srcs)
            {
                int c = 0;
                foreach (var b in r.bones) if (b != null && (b == wrist || b == forearmT || b.IsChildOf(wrist))) c++;
                if (c > most) { most = c; refR = r; }
            }
            Matrix4x4[] refBind;
            try { refBind = refR.sharedMesh.bindposes; } catch (Exception e) { why = "bind poses unreadable: " + e.Message; return null; }
            var bindOf = new Dictionary<Transform, Matrix4x4>();
            var rb = refR.bones;
            for (int i = 0; i < rb.Length && i < refBind.Length; i++) if (rb[i] != null && !bindOf.ContainsKey(rb[i])) bindOf[rb[i]] = refBind[i];

            var kT = new List<Transform>();
            kT.Add(wrist);
            bool hasForearm = forearmT != null && bindOf.ContainsKey(forearmT);
            if (hasForearm) kT.Add(forearmT);
            var stack = new Stack<Transform>();
            stack.Push(wrist);
            var order = new List<Transform>();
            while (stack.Count > 0)   // depth first, parents before children
            {
                var t = stack.Pop();
                for (int i = t.childCount - 1; i >= 0; i--) stack.Push(t.GetChild(i));
                if (t != wrist && bindOf.ContainsKey(t) && !IsUi(t)) order.Add(t);
            }
            kT.AddRange(order);
            var kIndex = new Dictionary<Transform, int>();
            for (int i = 0; i < kT.Count; i++) kIndex[kT[i]] = i;
            if (kT.Count < 8) { why = "only " + kT.Count + " hand bones with bind poses"; return null; }

            var sp = new HandSpec { Visual = "game hand" };
            Matrix4x4 bindW = bindOf[wrist];
            var rootM = new Matrix4x4[kT.Count];
            for (int i = 0; i < kT.Count; i++) rootM[i] = bindW * bindOf[kT[i]].inverse;
            for (int i = 0; i < kT.Count; i++)
            {
                var t = kT[i];
                int parent = -1;
                // nearest kept ancestor inside the wrist's subtree; the forearm copy (index 1) is a sibling of the wrist, nothing hangs below it
                if (i > 0 && !(hasForearm && i == 1))
                    for (var p = t.parent; p != null; p = p.parent)
                    {
                        int pi;
                        if (kIndex.TryGetValue(p, out pi) && !(hasForearm && pi == 1)) { parent = pi; break; }
                        if (p == wrist) break;
                    }
                var bs = new BoneSpec { Name = t.name, Parent = parent, M = rootM[i] };
                Vector3 lp, ls; Quaternion lr;
                Matrix4x4 local = parent >= 0 ? rootM[parent].inverse * rootM[i] : rootM[i];
                Decompose(local, out lp, out lr, out ls);
                bs.Pos = lp; bs.Rot = lr; bs.Scale = ls;
                sp.Bones.Add(bs);
            }

            // fingers: bones named like fingers below the wrist, one chain per finger by depth; the last three are the joints
            var groups = new List<int>[5];
            for (int f = 0; f < 5; f++) groups[f] = new List<int>();
            var depth = new int[kT.Count];
            for (int i = 0; i < kT.Count; i++)
            {
                int d = 0; for (var p = kT[i]; p != null && p != wrist; p = p.parent) d++;
                depth[i] = d;
            }
            for (int i = 1; i < kT.Count; i++)
            {
                if (hasForearm && i == 1) continue;
                int f = FingerOf(kT[i].name);
                if (f >= 0) groups[f].Add(i);
            }
            var fingerLog = new StringBuilder();
            for (int f = 0; f < 5; f++)
            {
                groups[f].Sort((a, b) => depth[a].CompareTo(depth[b]));
                var chain = new List<int>();
                int lastD = -1;
                foreach (var i in groups[f]) if (depth[i] > lastD) { chain.Add(i); lastD = depth[i]; }
                int start = Math.Max(0, chain.Count - 3);
                for (int j = 0; j < 3 && start + j < chain.Count; j++) sp.Fin[f * 3 + j] = chain[start + j];
                fingerLog.Append(f == 0 ? "" : ", ").Append(chain.Count);
            }
            int fingers = 0;
            for (int f = 0; f < 5; f++) if (sp.Fin[f * 3 + 1] >= 0) fingers++;
            if (fingers < 3) { why = "found finger chains: " + fingerLog + " (thumb..pinky), need at least three"; return null; }

            // size check: wrist -> middle fingertip should be a hand's length (0.15-0.23 m)
            int mi = -1;
            for (int j = 2; j >= 0; j--) if (sp.Fin[2 * 3 + j] >= 0) { mi = j; break; }
            float L;
            if (mi >= 1)
            {
                Vector3 pa = rootM[sp.Fin[6 + mi - 1]].GetColumn(3), pb = rootM[sp.Fin[6 + mi]].GetColumn(3);
                L = (pb + (pb - pa) * 0.8f).magnitude;
            }
            else { why = "middle finger has fewer than two bones"; return null; }
            float scale = 1f;
            string unitNote = "";
            if (L < 0.15f || L > 0.23f)
            {
                float ls = Mathf.Abs(refR.transform.lossyScale.x);
                scale = ls > 1e-6f ? ls : 1f;
                unitNote = " (hand " + L.ToString("0.###") + " units long, scaled by the renderer's " + scale.ToString("0.###") + ")";
                if (L * scale < 0.10f || L * scale > 0.30f) { why = "hand length " + (L * scale).ToString("0.000") + " m after unit scaling (" + L.ToString("0.###") + " units), not a hand"; return null; }
            }
            sp.Scale = scale;
            string fin = FinishSpec(sp, false, Vector3.zero);
            if (fin != null) { why = fin; return null; }

            // the mesh: weighted >= 0.5 on K and within 4 cm behind the wrist along the forearm axis
            Vector3 aRoot = hasForearm ? (-(Vector3)rootM[1].GetColumn(3)).normalized : (Vector3)(rootM[sp.Fin[6]].GetColumn(3)).normalized;
            if (aRoot.sqrMagnitude < 0.5f) aRoot = Vector3.forward;
            float back = 0.04f / scale;
            int totalV = 0, totalT = 0;
            var sb = new StringBuilder();
            foreach (var src in srcs)
            {
                string err;
                var d = MeshData.Read(src.sharedMesh, out err);
                if (d == null || d.Weights == null || d.Pos == null || d.Bind == null) { Plugin.Log.LogWarning("PhysicsHands: couldn't read '" + src.sharedMesh.name + "' (" + (err ?? "no bone weights") + ")"); continue; }
                int sv, st;
                var sec = Extract(src, d, kIndex, kT.Count, aRoot, back, out sv, out st);
                if (sec == null) continue;
                sp.Sections.Add(sec);
                totalV += sv; totalT += st;
                sb.Append(sb.Length > 0 ? ", " : "").Append(src.name).Append(' ').Append(sv).Append('v');
            }
            if (sp.Sections.Count == 0 || totalV < 30) { why = "no hand vertices in the player's skins (" + srcs.Count + " tried)"; foreach (var s in sp.Sections) if (s.Mesh != null) UnityEngine.Object.Destroy(s.Mesh); sp.Sections.Clear(); return null; }
            sp.Note = "game hand: " + totalV + " verts, " + totalT + " tris, " + kT.Count + " bones, fingers per chain " + fingerLog + ", hand " + (L * scale).ToString("0.000") + " m, scale " + scale.ToString("0.###") + unitNote + " [" + sb + "], " + sp.Note;
            return sp;
        }

        static void PutW(ref BoneWeight b, int slot, int idx, float w)
        {
            switch (slot)
            {
                case 0: b.boneIndex0 = idx; b.weight0 = w; break;
                case 1: b.boneIndex1 = idx; b.weight1 = w; break;
                case 2: b.boneIndex2 = idx; b.weight2 = w; break;
                default: b.boneIndex3 = idx; b.weight3 = w; break;
            }
        }

        // One source skin -> a hand mesh on the K bones: kept vertices (K weight >= .5, near the wrist), kept triangles (all three
        // kept), compacted; bone weights remapped to K and renormalised; one submesh per source submesh that kept triangles.
        static Section Extract(SkinnedMeshRenderer src, MeshData d, Dictionary<Transform, int> kIndex, int nK, Vector3 aRoot, float back, out int verts, out int tris)
        {
            verts = tris = 0;
            var bones = src.bones;
            var kOf = new int[bones.Length];
            var srcOfK = new int[nK];
            for (int i = 0; i < srcOfK.Length; i++) srcOfK[i] = -1;
            for (int i = 0; i < bones.Length; i++)
            {
                int k;
                kOf[i] = bones[i] != null && kIndex.TryGetValue(bones[i], out k) ? k : -1;
                if (kOf[i] >= 0 && srcOfK[kOf[i]] < 0) srcOfK[kOf[i]] = i;
            }
            if (srcOfK[0] < 0 || srcOfK[0] >= d.Bind.Length) return null;
            Matrix4x4 bw = d.Bind[srcOfK[0]];
            int nv = d.Pos.Length;
            if (d.Weights.Length != nv) return null;
            var kept = new bool[nv];
            for (int v = 0; v < nv; v++)
            {
                var w = d.Weights[v];
                float wk = 0f;
                if (w.weight0 > 0f && w.boneIndex0 < kOf.Length && kOf[w.boneIndex0] >= 0) wk += w.weight0;
                if (w.weight1 > 0f && w.boneIndex1 < kOf.Length && kOf[w.boneIndex1] >= 0) wk += w.weight1;
                if (w.weight2 > 0f && w.boneIndex2 < kOf.Length && kOf[w.boneIndex2] >= 0) wk += w.weight2;
                if (w.weight3 > 0f && w.boneIndex3 < kOf.Length && kOf[w.boneIndex3] >= 0) wk += w.weight3;
                if (wk < 0.5f) continue;
                if (Vector3.Dot(bw.MultiplyPoint3x4(d.Pos[v]), aRoot) < -back) continue;
                kept[v] = true;
            }
            var used = new bool[nv];
            var subs = new List<List<int>>();
            var subIdx = new List<int>();
            for (int s = 0; s < d.Sub.Count; s++)
            {
                var t = d.Sub[s];
                List<int> keep = null;
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    int a = t[i], b = t[i + 1], c = t[i + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= nv || b >= nv || c >= nv || !kept[a] || !kept[b] || !kept[c]) continue;
                    if (keep == null) keep = new List<int>();
                    keep.Add(a); keep.Add(b); keep.Add(c);
                    used[a] = used[b] = used[c] = true;
                }
                if (keep != null) { subs.Add(keep); subIdx.Add(s); }
            }
            var map = new int[nv];
            var order = new List<int>();
            for (int v = 0; v < nv; v++) { map[v] = used[v] ? order.Count : -1; if (used[v]) order.Add(v); }
            if (order.Count < 10) return null;

            int n = order.Count;
            var m = new Mesh { name = Pfx + "_hand_" + src.sharedMesh.name };
            if (n > 65535) m.indexFormat = IndexFormat.UInt32;
            var pos = new Vector3[n];
            for (int i = 0; i < n; i++) pos[i] = d.Pos[order[i]];
            m.vertices = pos;
            if (d.Nrm != null && d.Nrm.Length == nv) { var a = new Vector3[n]; for (int i = 0; i < n; i++) a[i] = d.Nrm[order[i]]; m.normals = a; }
            if (d.Tan != null && d.Tan.Length == nv) { var a = new Vector4[n]; for (int i = 0; i < n; i++) a[i] = d.Tan[order[i]]; m.tangents = a; }
            if (d.Uv != null && d.Uv.Length == nv) { var a = new Vector2[n]; for (int i = 0; i < n; i++) a[i] = d.Uv[order[i]]; m.uv = a; }
            for (int c = 1; c < 4; c++)
                if (d.Uvs[c] != null && d.Uvs[c].Count == nv) { var l = new List<Vector4>(n); for (int i = 0; i < n; i++) l.Add(d.Uvs[c][order[i]]); m.SetUVs(c, l); }
            if (d.Col != null && d.Col.Length == nv) { var a = new Color[n]; for (int i = 0; i < n; i++) a[i] = d.Col[order[i]]; m.colors = a; }
            var bwArr = new BoneWeight[n];
            for (int i = 0; i < n; i++)
            {
                var w = d.Weights[order[i]];
                var o = new BoneWeight();
                float tot = 0f; int slot = 0;
                for (int q = 0; q < 4; q++)
                {
                    int bi = q == 0 ? w.boneIndex0 : q == 1 ? w.boneIndex1 : q == 2 ? w.boneIndex2 : w.boneIndex3;
                    float wt = q == 0 ? w.weight0 : q == 1 ? w.weight1 : q == 2 ? w.weight2 : w.weight3;
                    if (wt <= 0f || bi < 0 || bi >= kOf.Length || kOf[bi] < 0) continue;
                    PutW(ref o, slot++, kOf[bi], wt); tot += wt;
                }
                if (tot > 0f) { o.weight0 /= tot; o.weight1 /= tot; o.weight2 /= tot; o.weight3 /= tot; }
                else { o.boneIndex0 = 0; o.weight0 = 1f; }
                bwArr[i] = o;
            }
            m.boneWeights = bwArr;
            var bind = new Matrix4x4[nK];
            for (int k = 0; k < nK; k++) bind[k] = srcOfK[k] >= 0 && srcOfK[k] < d.Bind.Length ? d.Bind[srcOfK[k]] : Matrix4x4.identity;
            m.bindposes = bind;
            m.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++)
            {
                var l = subs[s];
                var arr = new int[l.Count];
                for (int i = 0; i < arr.Length; i++) arr[i] = map[l[i]];
                m.SetTriangles(arr, s, false);
                tris += arr.Length / 3;
            }
            m.RecalculateBounds();
            var srcMats = src.sharedMaterials;
            var mats = new Material[subs.Count];
            for (int s = 0; s < subs.Count; s++) mats[s] = srcMats != null && srcMats.Length > 0 ? srcMats[Mathf.Min(subIdx[s], srcMats.Length - 1)] : null;
            verts = n;
            return new Section { Mesh = m, Mats = mats, From = src.name };
        }

        // ================= physics survey and layers =================

        static bool _surveyed, _layersApplied, _taken;
        static int _layerHand = -1, _layerHer = -1;
        static readonly bool[] _origHand = new bool[32], _origHer = new bool[32];
        static float _retryAt;

        // Why physics can't be used, or null. Logs the facts once per session.
        static string Prepare()
        {
            float dt = Time.fixedDeltaTime;
            var mode = Physics.simulationMode;
            var used = new bool[32];
            int colliders = 0;
            foreach (var o in Resources.FindObjectsOfTypeAll(typeof(Collider)))
            {
                var c = o as Collider;
                if (c == null || !c.gameObject.scene.IsValid()) continue;
                used[c.gameObject.layer] = true; colliders++;
            }
            var names = new StringBuilder();
            for (int i = 0; i < 32; i++) { var n = LayerMask.LayerToName(i); if (n.Length > 0) names.Append(names.Length > 0 ? ", " : "").Append(i).Append('=').Append(n); }
            var usedS = new StringBuilder();
            for (int i = 0; i < 32; i++) if (used[i]) usedS.Append(usedS.Length > 0 ? "," : "").Append(i);
            if (!_surveyed)
            {
                _surveyed = true;
                Plugin.Log.LogInfo("PhysicsHands survey: fixedDeltaTime " + dt.ToString("0.#####") + " (" + (dt > 0f ? 1f / dt : 0f).ToString("0.#") + " Hz), timeScale " + Time.timeScale
                                   + ", simulationMode " + mode + ", solver iterations " + Physics.defaultSolverIterations + "/" + Physics.defaultSolverVelocityIterations
                                   + ", gravity " + Physics.gravity + ", " + colliders + " colliders in the scene on layers [" + usedS + "], named layers [" + names + "]");
            }
            if (!(dt > 0f)) return "fixedDeltaTime is " + dt;
            if (mode != SimulationMode.FixedUpdate) return "physics doesn't simulate automatically (simulationMode " + mode + ")";

            int a = -1, b = -1;
            for (int i = 31; i >= 8; i--)
            {
                if (used[i] || LayerMask.LayerToName(i).Length > 0) continue;
                if (a < 0) a = i; else { b = i; break; }
            }
            if (b < 0) return "no two unused physics layers (colliders sit on [" + usedS + "])";
            _layerHand = a; _layerHer = b;
            for (int k = 0; k < 32; k++)
            {
                _origHand[k] = Physics.GetIgnoreLayerCollision(_layerHand, k);
                _origHer[k] = Physics.GetIgnoreLayerCollision(_layerHer, k);
            }
            for (int k = 0; k < 32; k++) Physics.IgnoreLayerCollision(_layerHand, k, k != _layerHer);
            for (int k = 0; k < 32; k++) Physics.IgnoreLayerCollision(_layerHer, k, k != _layerHand);
            _layersApplied = true; _taken = true;
            Plugin.Log.LogInfo("PhysicsHands: layers " + _layerHand + " (hands) and " + _layerHer + " (her body): each collides only with the other; the earlier matrix is restored on release");
            MakeMaterials(_s != null ? _s.Friction : 0.5f);
            return null;
        }

        // ---- friction (0.7.8) ----
        // PhysX's default material (0.6 / 0.6) slid a resting hand off her moving thigh. One material for every hand collider and one for
        // every collider of hers, the same numbers, combine Average: static = dynamic = 0.75 x f, f = [Hands] Friction (default 0.5). No stick
        // threshold above the sliding friction (a higher static value made a hand cling then break loose = stick-slip jitter under the follow
        // force). The slider writes the fields in place (the shapes sharing the material follow), nothing is recreated.
        // 0.8.0: the fingers get their own low-friction material (0.15, combine Minimum: PhysX gives Minimum priority over her Average, so
        // a fingertip slides on skin as in DynamicLimbs while the palm, on _handMat, still carries the hand along with her). The slider
        // doesn't touch it.
        static PhysicMaterial _handMat, _herMat, _fingerMat;
        static float _matF = -1f;
        const float FingerFriction = 0.15f;

        static void MakeMaterials(float f)
        {
            f = Mathf.Clamp01(f);
            if (_fingerMat == null)
                _fingerMat = new PhysicMaterial(Pfx + "_finger") { dynamicFriction = FingerFriction, staticFriction = FingerFriction, frictionCombine = PhysicMaterialCombine.Minimum, bounciness = 0f, bounceCombine = PhysicMaterialCombine.Minimum };
            if (_handMat == null) _handMat = new PhysicMaterial(Pfx + "_hand") { frictionCombine = PhysicMaterialCombine.Average, bounciness = 0f, bounceCombine = PhysicMaterialCombine.Minimum };
            if (_herMat == null) _herMat = new PhysicMaterial(Pfx + "_her") { frictionCombine = PhysicMaterialCombine.Average, bounciness = 0f, bounceCombine = PhysicMaterialCombine.Minimum };
            SetFriction(f);
            LogOnce("friction", "friction " + (0.75f * f).ToString("0.00") + " static = dynamic (Average, no stick threshold) on the hands and her colliders");
        }

        static void SetFriction(float f)
        {
            _matF = f;
            if (_handMat != null) { _handMat.dynamicFriction = 0.75f * f; _handMat.staticFriction = 0.75f * f; }
            if (_herMat != null) { _herMat.dynamicFriction = 0.75f * f; _herMat.staticFriction = 0.75f * f; }
        }

        static void RestoreLayers()
        {
            if (!_layersApplied) return;
            for (int k = 0; k < 32; k++) Physics.IgnoreLayerCollision(_layerHand, k, _origHand[k]);
            for (int k = 0; k < 32; k++) Physics.IgnoreLayerCollision(_layerHer, k, _origHer[k]);
            _layersApplied = false;
        }

        // ================= a hand =================

        sealed class Hand
        {
            public bool Left, Fallback, Kin, CollideOn = true;
            public string FallbackWhy = "";
            public HandSpec Spec;
            public GameObject PGo, VGo;
            public Transform PRoot, VRoot;
            public Transform[] PBones, VBones;
            public Rigidbody Rb;
            public readonly List<Transform> ColT = new List<Transform>();
            public readonly List<Collider> Cols = new List<Collider>();
            public readonly List<Renderer> Rends = new List<Renderer>();
            public readonly List<Mesh> Meshes = new List<Mesh>();
            public readonly List<GameObject> Marks = new List<GameObject>();
            public Quaternion[] Rest = new Quaternion[15];
            public Vector3[] AxisLocal = new Vector3[15];
            public Vector3[] DirLocal = new Vector3[15];   // per finger*3+joint: the finger's direction in the bone's axes (joint secondary axis)
            public int BuiltMesh;
            public CapSource Src;                          // 0.7.7: who these soft capsules belong to (VrHands.NewSource), made with the first RebuildCaps, dropped with the hand
            // the finger rig (PhysicsHands.Rig.cs)
            public bool RigOn, RigFailed;
            public readonly List<Fing> Rig = new List<Fing>();
            public readonly Fing[] FingAt = new Fing[15];            // the rig's body per finger*3+joint (null: none)
            public readonly Quaternion[] RelScratch = new Quaternion[15];
            public readonly float[] FCurl = new float[5];             // per finger: the curl the joints are asked for (UpdateHand: command, conform, ease)
            public readonly float[] FWritten = new float[5];
            public readonly float[] FApplied = { -1f, -1f, -1f, -1f, -1f };   // kinematic fingers: the curl last written to the bones
            // 0.8.0 finger conform (section 5): one fan per finger (null: no such finger), the first touching step of last frame per finger
            // (hysteresis), and the 10 s counters
            public readonly FingerFan[] Fan = new FingerFan[5];
            public readonly int[] FanStep = { -1, -1, -1, -1, -1 };
            public readonly int[] FHeldN = new int[5];                // frames each finger was held by contact
            public int FContactN, FGiveN, FFlipN; public float FContactSum, FGiveSum;
            // her capsules the soft zones read (RebuildCaps) as the visual-bone-local endpoints the controller-pose ghost is made from
            public int[] GhostBone = new int[0]; public Vector3[] GhostA = new Vector3[0], GhostB = new Vector3[0]; public float[] GhostR = new float[0]; public int GhostN;
            // the drawn-offset gate (section 4): weight of the raw offset that is shown, and the 10 s statistics raw against shown
            public float VisW;
            public int OffN, FreeN; public float RawMmSum, RawDegSum, ShownMmSum, ShownDegSum, FreeMaxMm, FreeMaxDeg;
            public float JitRawSum, JitRawMax, JitRawQSum, JitRawQMax; public Vector3 RawPrev; public Quaternion RawPrevQ = Quaternion.identity;
            public float TotalMass = 0.35f, BadT, StuckT, SeenFixed = -1f, CapSum, ResistMax;
            // 10 s diagnostics of the follow loops: velocity errors and how often they flip sign step to step (an oscillation at the
            // step rate flips nearly every step; a healthy loop rarely does)
            public float VErrSum, VErrMax, WErrSum, WErrMax; public int VFlipN, WFlipN; public Vector3 VErrPrev, WErrPrev; public bool ErrPrevOk;
            public int CapN, CapSat;
            public bool CurValid;
            // the target used at the last fixed step (for the visual offset) and the teleport rules
            public bool HaveFixed, HaveLast;
            public Vector3 FixedPos, LastTarget;
            public Quaternion FixedRot = Quaternion.identity;
            public float FarTime, NextUpgrade, NextMat;
            // 0.7.8 follow fix. Trk: the controller target sampled once per rendered frame with its velocity (extrapolated to each fixed step).
            // Off0/Off1: how far the body stood from the target it aimed at, at the end of the last two fixed steps (the visual interpolates
            // between them); StepsDone counts the steps that aimed, OffSeen the ones already captured. Vis*: what this frame's visual added.
            public Track Trk;
            public int StepsDone, OffSeen, VisFrame = -1;
            public bool OffValid, VisHave, JointWas = true;
            public Vector3 Off0P, Off1P, VisP, VisPrev; public Quaternion VisPrevQ = Quaternion.identity;
            public Quaternion Off0Q = Quaternion.identity, Off1Q = Quaternion.identity, VisQ = Quaternion.identity;
            // 10 s diagnostics: steps at the force cap in free air / touching her, the gap there, and the visual offset's frame-to-frame change
            public int CapFreeN, CapTouchN, CapSatFree, CapSatTouch, JitN;
            public float GapAtCapSum, JitSum, JitMax, JitQSum, JitQMax;   // JitQ*: the same for the offset's rotation (degrees)
        }

        static Hand _left, _right;
        static HandsSettings _s;

        static void Target(Transform c, Vector3 off, Vector3 rot, Quaternion align, out Vector3 p, out Quaternion q)
        {
            var cr = c.rotation;
            p = c.position + cr * off;                       // the wrist, in the controller's axes
            q = cr * Quaternion.Euler(rot) * align;          // the extra rotation, on top of the automatic hand alignment
        }

        static void SetLayerRec(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRec(t.GetChild(i), layer);
        }

        static Transform[] MakeBones(Transform root, HandSpec sp)
        {
            var t = new Transform[sp.Bones.Count];
            for (int i = 0; i < t.Length; i++)
            {
                var b = sp.Bones[i];
                var tr = new GameObject(b.Name).transform;
                tr.SetParent(b.Parent >= 0 ? t[b.Parent] : root, false);
                tr.localPosition = b.Pos; tr.localRotation = b.Rot; tr.localScale = b.Scale;
                t[i] = tr;
            }
            return t;
        }

        static Material _skinMat;
        static Material SkinMat()
        {
            if (_skinMat != null) return _skinMat;
            var sh = Shader.Find("HDRP/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            if (sh == null)
            {
                var prim = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _skinMat = new Material(prim.GetComponent<Renderer>().sharedMaterial);
                UnityEngine.Object.Destroy(prim);
            }
            else _skinMat = new Material(sh);
            _skinMat.name = Pfx + "_skin";
            var tone = new Color(0.86f, 0.66f, 0.55f, 1f);
            if (_skinMat.HasProperty("_BaseColor")) _skinMat.SetColor("_BaseColor", tone);
            if (_skinMat.HasProperty("_Color")) _skinMat.SetColor("_Color", tone);
            if (_skinMat.HasProperty("_Smoothness")) _skinMat.SetFloat("_Smoothness", 0.35f);
            return _skinMat;
        }

        // Chooses the hand's spec (log which visual) and builds it at the controller's pose; null if it can't.
        static Hand MakeHand(bool left, Transform ctl, HandsSettings s, HandSpec given)
        {
            HandSpec sp = given;
            bool fallback = false; string why = "";
            if (sp == null && s.Mesh == 0)
            {
                try { sp = GameSpec(left, out why); }
                catch (Exception e) { why = e.GetType().Name + ": " + e.Message; sp = null; }
                if (sp == null) { fallback = true; LogOnce("fb" + left, (left ? "left" : "right") + " hand: game hand unavailable (" + why + "), primitive hand instead"); }
            }
            if (sp == null) sp = SyntheticSpec(left);
            var h = new Hand { Left = left, Spec = sp, Fallback = fallback, FallbackWhy = why, BuiltMesh = s.Mesh };
            h.NextUpgrade = Time.unscaledTime + 10f;
            try { BuildHand(h, ctl, s); }
            catch (Exception e)
            {
                Plugin.Log.LogError("PhysicsHands: building the " + (left ? "left" : "right") + " hand failed: " + e);
                DestroyHand(h);
                return null;
            }
            Plugin.Log.LogInfo("PhysicsHands: " + (left ? "left" : "right") + " hand: " + sp.Visual + ", " + sp.Cols.Count + " colliders, " + sp.Note);
            return h;
        }

        static void BuildHand(Hand h, Transform ctl, HandsSettings s)
        {
            var sp = h.Spec;
            Vector3 tp; Quaternion tq;
            Target(ctl, h.Left ? s.LeftPos : s.RightPos, h.Left ? s.LeftRot : s.RightRot, sp.Align, out tp, out tq);
            string side = h.Left ? "L" : "R";

            // the physics body: bones (they curl) with a child per collider
            h.PGo = new GameObject(Pfx + "_" + side + "_body");
            h.PRoot = h.PGo.transform;
            h.PRoot.SetPositionAndRotation(tp, tq);
            h.PRoot.localScale = Vector3.one * sp.Scale;
            h.PBones = MakeBones(h.PRoot, sp);
            foreach (var sh in sp.Cols)
            {
                var bone = h.PBones[sh.Bone];
                var go = new GameObject(Pfx + "_col_" + side + "_" + sh.Tag);
                go.transform.SetParent(bone, false);
                Collider col;
                if (sh.Box)
                {
                    go.transform.position = h.PRoot.TransformPoint(sh.A);
                    go.transform.rotation = h.PRoot.rotation * sh.Rot;
                    var bc = go.AddComponent<BoxCollider>();
                    bc.size = sh.Size / sp.Scale;
                    col = bc;
                }
                else
                {
                    Vector3 a = h.PRoot.TransformPoint(sh.A), b = h.PRoot.TransformPoint(sh.B);
                    Vector3 d = b - a; float len = d.magnitude;
                    go.transform.position = (a + b) * 0.5f;
                    go.transform.rotation = len > 1e-6f ? Quaternion.FromToRotation(Vector3.up, d / len) : Quaternion.identity;
                    var cc = go.AddComponent<CapsuleCollider>();
                    cc.direction = 1;
                    cc.radius = sh.R / sp.Scale;
                    cc.height = (len + 2f * sh.R) / sp.Scale;
                    col = cc;
                }
                if (_handMat == null || _fingerMat == null) MakeMaterials(s.Friction);
                col.sharedMaterial = sh.Tag == "palm" ? _handMat : _fingerMat;
                h.ColT.Add(go.transform); h.Cols.Add(col);
            }
            SetLayerRec(h.PRoot, _layerHand);
            var rb = h.PGo.AddComponent<Rigidbody>();
            rb.mass = PalmMass; rb.useGravity = false; rb.drag = 0f; rb.angularDrag = 0f;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.maxAngularVelocity = 50f; rb.maxDepenetrationVelocity = 2f;
            rb.solverIterations = 16; rb.solverVelocityIterations = 4; rb.sleepThreshold = 0f;
            rb.centerOfMass = Vector3.zero;   // so the velocity we command is the wrist's
            h.Rb = rb;
            h.TotalMass = PalmMass;
            h.PGo.AddComponent<PhysicsHandProbe>().Left = h.Left;

            // the visual: the same skeleton, posed at the (offset) controller target each frame
            h.VGo = new GameObject(Pfx + "_" + side + "_visual");
            h.VRoot = h.VGo.transform;
            h.VRoot.SetPositionAndRotation(tp, tq);
            h.VRoot.localScale = Vector3.one * sp.Scale;
            h.VBones = MakeBones(h.VRoot, sp);
            if (sp.Sections.Count > 0)
            {
                foreach (var sec in sp.Sections)
                {
                    var go = new GameObject(Pfx + "_mesh_" + sec.From);
                    go.transform.SetParent(h.VRoot, false);
                    var smr = go.AddComponent<SkinnedMeshRenderer>();
                    smr.sharedMesh = sec.Mesh;
                    smr.bones = h.VBones;
                    smr.rootBone = h.VBones[0];
                    var mats = new Material[sec.Mats.Length];
                    for (int i = 0; i < mats.Length; i++) mats[i] = sec.Mats[i] != null ? sec.Mats[i] : SkinMat();
                    smr.sharedMaterials = mats;
                    smr.updateWhenOffscreen = true;
                    smr.quality = SkinQuality.Bone4;
                    h.Rends.Add(smr);
                    h.Meshes.Add(sec.Mesh);
                }
            }
            else
            {
                foreach (var sh in sp.Prims)
                {
                    var go = GameObject.CreatePrimitive(sh.Box ? PrimitiveType.Cube : PrimitiveType.Capsule);
                    go.name = Pfx + "_prim_" + sh.Tag;
                    foreach (var comp in go.GetComponents<Component>())   // a visual must never be touched or touch anything
                        if (comp != null && comp.GetType().Name.EndsWith("Collider", StringComparison.Ordinal)) UnityEngine.Object.DestroyImmediate(comp);
                    var bone = h.VBones[sh.Bone];
                    go.transform.SetParent(bone, false);
                    if (sh.Box)
                    {
                        go.transform.position = h.VRoot.TransformPoint(sh.A);
                        go.transform.rotation = h.VRoot.rotation * sh.Rot;
                        go.transform.localScale = sh.Size / sp.Scale;
                    }
                    else
                    {
                        Vector3 a = h.VRoot.TransformPoint(sh.A), b = h.VRoot.TransformPoint(sh.B);
                        Vector3 d = b - a; float len = d.magnitude;
                        go.transform.position = (a + b) * 0.5f;
                        go.transform.rotation = len > 1e-6f ? Quaternion.FromToRotation(Vector3.up, d / len) : Quaternion.identity;
                        go.transform.localScale = new Vector3(2f * sh.R, (len + 2f * sh.R) * 0.5f, 2f * sh.R) / sp.Scale;
                    }
                    var r = go.GetComponent<Renderer>();
                    r.sharedMaterial = SkinMat();
                    r.shadowCastingMode = ShadowCastingMode.On;
                    h.Rends.Add(r);
                }
            }

            // rest rotations and curl axes (in each bone's own axes)
            for (int f = 0; f < 5; f++)
                for (int j = 0; j < 3; j++)
                {
                    int i = f * 3 + j, bi = sp.Fin[i];
                    if (bi < 0) { h.Rest[i] = Quaternion.identity; h.AxisLocal[i] = Vector3.right; continue; }
                    Vector3 p0, sc; Quaternion rr;
                    Decompose(sp.Bones[bi].M, out p0, out rr, out sc);
                    h.Rest[i] = sp.Bones[bi].Rot;
                    h.AxisLocal[i] = Quaternion.Inverse(rr) * sp.FAxis[f];
                    h.DirLocal[i] = Quaternion.Inverse(rr) * sp.FDir[f];
                }
            BuildFans(h);
            h.Kin = false;
            h.CollideOn = true;
            h.RigFailed = false;
            h.JointWas = s.JointedFingers;
            if (s.JointedFingers) EnableRig(h);
            if (_dbgOn) AddMarkers(h);
            _statusDirty = true;
        }

        static void DestroyHand(Hand h)
        {
            if (h == null) return;
            RemoveMarkers(h);
            if (h.PGo != null) UnityEngine.Object.Destroy(h.PGo);
            if (h.VGo != null) UnityEngine.Object.Destroy(h.VGo);
            foreach (var m in h.Meshes) if (m != null) UnityEngine.Object.Destroy(m);
            h.Meshes.Clear(); h.Rends.Clear(); h.Cols.Clear(); h.ColT.Clear(); h.Rig.Clear(); h.RigOn = false;
            foreach (var sec in h.Spec.Sections) if (sec.Mesh != null) UnityEngine.Object.Destroy(sec.Mesh);
            h.PGo = h.VGo = null;
            VrHands.DropSource(h.Src);
            h.Src = null;
        }

        // The visual capsules for the soft zones: palm (up to three capsules, radius PalmThick) + finger segments on the visual bones.
        // Every capsule carries the hand's CapSource (never gated: the visual may be hidden while the physics hand still touches her).
        static void RebuildCaps()
        {
            bool had = Capsules.Count > 0;
            Capsules.Clear();
            LeftWrist = RightWrist = null;
            foreach (var h in new[] { _left, _right })
            {
                if (h == null || h.VGo == null) continue;
                if (h.Left) LeftWrist = h.VBones[0]; else RightWrist = h.VBones[0];
                if (h.Src == null) h.Src = VrHands.NewSource(h.Left ? "your left physics hand" : "your right physics hand", "vr", false);
                int nc = h.Spec.Caps.Count;
                if (h.GhostBone.Length != nc) { h.GhostBone = new int[nc]; h.GhostA = new Vector3[nc]; h.GhostB = new Vector3[nc]; h.GhostR = new float[nc]; }
                h.GhostN = 0;
                foreach (var c in h.Spec.Caps)
                {
                    var bone = h.VBones[c.Bone];
                    // from the REST pose (the bone's root-space matrix), not from where the bones stand now: a rebuild while the fingers are
                    // curled (the other hand was rebuilt) would otherwise bake the curl into the capsules
                    Matrix4x4 inv = h.Spec.Bones[c.Bone].M.inverse;
                    Vector3 la = inv.MultiplyPoint3x4(c.A), lb = inv.MultiplyPoint3x4(c.B);
                    Capsules.Add(new SoftCapsule { T = bone, LocalA = la, LocalB = lb, Radius = c.R, Owner = "PhysicsHands", Kind = "vr", Src = h.Src });
                    // the same capsule for FillGhost: bone-local endpoints on the visual bones, re-posed at the controller each frame
                    h.GhostBone[h.GhostN] = c.Bone; h.GhostA[h.GhostN] = la; h.GhostB[h.GhostN] = lb; h.GhostR[h.GhostN] = c.R; h.GhostN++;
                }
                h.Src.CapCount = h.Spec.Caps.Count;
            }
            if (had || Capsules.Count > 0) Version++;
            Active = _left != null || _right != null;
        }

        // ================= controller models =================

        sealed class Hider
        {
            public readonly List<Renderer> R = new List<Renderer>();
            public readonly List<bool> Was = new List<bool>();
            public readonly HashSet<Renderer> Seen = new HashSet<Renderer>();
            public float NextScan;
        }

        static readonly Hider _hideL = new Hider(), _hideR = new Hider();

        // Renderers under VR_HandController.controller_left/right (not UI, not line renderers) off, re-asserted every frame.
        static void HideController(Hider hd, bool left, float now)
        {
            if (now >= hd.NextScan)
            {
                hd.NextScan = now + 2f;
                var hc = HandController();
                var anim = hc != null ? Get(hc, left ? "controller_left" : "controller_right") as Animator : null;
                if (anim != null)
                {
                    int before = hd.R.Count;
                    foreach (var r in anim.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null || r is LineRenderer || IsUi(r.transform) || hd.Seen.Contains(r)) continue;
                        hd.Seen.Add(r); hd.R.Add(r); hd.Was.Add(r.enabled);
                    }
                    if (hd.R.Count != before) Plugin.Log.LogInfo("PhysicsHands: hiding " + hd.R.Count + " controller renderers on " + (left ? "controller_left" : "controller_right"));
                }
                else LogOnce("nohc" + left, "no controller model found under VR_HandController." + (left ? "controller_left" : "controller_right") + ": nothing to hide");
            }
            for (int i = 0; i < hd.R.Count; i++)
            {
                var r = hd.R[i];
                if (r != null && r.enabled) r.enabled = false;
            }
        }

        static void RestoreHide(Hider hd)
        {
            for (int i = 0; i < hd.R.Count; i++) if (hd.R[i] != null) hd.R[i].enabled = hd.Was[i];
            hd.R.Clear(); hd.Was.Clear(); hd.Seen.Clear(); hd.NextScan = 0f;
        }

        // ================= her body: kinematic colliders (the geometry lives in HerBody.cs) =================

        // The PhysX half of one of her segments (HerBody.Seg): a kinematic rigidbody on a GameObject whose origin is the segment's joint A
        // (axis segments; spheres: the sphere's centre) so the capsule and the convex hull share one layout (HerBody.SegPose). The capsule
        // always exists (a sphere is a capsule of length 0); the hull segments get a convex MeshCollider cooked from HerBody's skin points,
        // and whichever one HullColliders asks for (and the cook allowed) is the one that is enabled.
        sealed class SegBody
        {
            public HerBody.Seg S;
            public GameObject Go; public Transform T; public Rigidbody Rb; public CapsuleCollider Cap;
            public MeshCollider Hull; public Mesh HullMesh;
            public bool HullOk, HullTried, UsingHull, Queued; public float CookedInset;
            public float RBase, Ratio = 1f, H = -1f;   // capsule radius at the current softness; her bone's scale since the build; capsule height written
            public Vector3 Center = new Vector3(float.NaN, 0f, 0f);   // capsule centre written
            public Vector3 ScaleWritten = Vector3.one, LastPos; public bool Placed;
            public GameObject Mark, HMark;
        }

        static readonly List<SegBody> _bodies = new List<SegBody>();
        static readonly Dictionary<Collider, SegBody> _segOfCol = new Dictionary<Collider, SegBody>();
        static readonly Queue<SegBody> _cookQueue = new Queue<SegBody>();
        static readonly System.Diagnostics.Stopwatch _cookSw = new System.Diagnostics.Stopwatch();
        static GameObject _herGo;
        static bool _herBuilt;                    // the bodies exist (HerBody has the geometry)
        static int _herVersion = -1;              // HerBody.BuildVersion the bodies were made from
        static float _herNext, _herSoftness = -1f;
        static string _herNote = "";
        static bool _herBuiltSeen; static string _herNoteSeen = "";   // what the status line last said about HerBody
        static bool _hullWant;                    // HullColliders as last applied
        static int _cookOk, _cookFail, _cookFrames; static float _cookMs; static bool _cookLogged;
        static int _herSampleFrame = -1;          // the rendered frame her segments were last sampled in (once per frame, at its first fixed step)

        // One body per HerBody segment: a capsule (spheres are capsules of length 0) at the skin radius minus the inset; the hulls of the
        // segments that have them are queued and cooked a couple per frame (the capsule collides meanwhile).
        static void BuildHerBodies(HandsSettings s)
        {
            DestroyHer();
            _herVersion = HerBody.BuildVersion;
            _herGo = new GameObject(Pfx + "_her");
            if (_herMat == null) MakeMaterials(s.Friction);
            foreach (var g in HerBody.Segs)
            {
                var b = new SegBody { S = g };
                b.RBase = Mathf.Max(0.012f, g.RSkin - HerBody.Inset(g, s.Softness));
                b.Go = new GameObject(HerPfx + g.Name);
                b.Go.layer = _layerHer;
                b.T = b.Go.transform;
                b.T.SetParent(_herGo.transform, false);
                b.Cap = b.Go.AddComponent<CapsuleCollider>();
                b.Cap.direction = 1; b.Cap.radius = b.RBase; b.Cap.height = 2f * b.RBase;
                b.Cap.sharedMaterial = _herMat;
                b.Rb = b.Go.AddComponent<Rigidbody>();
                b.Rb.isKinematic = true; b.Rb.useGravity = false;
                b.Rb.interpolation = RigidbodyInterpolation.None;
                _bodies.Add(b);
                _segOfCol[b.Cap] = b;
            }
            _herBuilt = true;
            _herSoftness = s.Softness;
            _herNote = "";
            _statusDirty = true;
            _hullWant = s.HullColliders;
            _cookOk = _cookFail = _cookFrames = 0; _cookMs = 0f; _cookLogged = false;
            QueueHulls(s);
            if (_dbgOn) AddHerMarkers();
        }

        static void DestroyHer()
        {
            RemoveHerMarkers();
            foreach (var b in _bodies)
            {
                if (b.Go != null) UnityEngine.Object.Destroy(b.Go);
                if (b.HullMesh != null) UnityEngine.Object.Destroy(b.HullMesh);
            }
            _bodies.Clear();
            _segOfCol.Clear();
            _cookQueue.Clear();
            if (_herGo != null) UnityEngine.Object.Destroy(_herGo);
            _herGo = null; _herBuilt = false; _herVersion = -1;
            for (int i = 0; i < HerBody.Segs.Count; i++) HerBody.Segs[i].Trk.Reset();   // the sampled poses belong to the bodies that followed them
            HerBody.ClearPhys();   // no bodies, no contacts: the last push must not linger in HerBody
            _statusDirty = true;
        }

        // ---- convex hulls (0.7.8) ----

        // Queues the hull segments whose hull is missing or was cooked for another inset (softness moved), when hulls are wanted at all.
        // A segment without HullPts (no hull from HerBody) simply keeps its capsule.
        static void QueueHulls(HandsSettings s)
        {
            if (!s.HullColliders) return;
            for (int i = 0; i < _bodies.Count; i++)
            {
                var b = _bodies[i];
                if (b.Queued || b.Go == null || !b.S.Hull || b.S.HullPts == null || b.S.HullTris == null) continue;
                if (b.HullTried && Mathf.Abs(b.CookedInset - HerBody.Inset(b.S, s.Softness)) <= 0.002f) continue;
                b.Queued = true;
                _cookQueue.Enqueue(b);
            }
        }

        // Per rendered frame: cooks at most CookPerFrame queued hulls (PhysX cooking is a few ms for 130-200 points).
        static void CookStep(HandsSettings s)
        {
            if (_cookQueue.Count == 0) return;
            _cookSw.Reset(); _cookSw.Start();
            int n = 0;
            while (n < CookPerFrame && _cookQueue.Count > 0)
            {
                var b = _cookQueue.Dequeue();
                b.Queued = false;
                if (b.Go == null) continue;
                try { CookHull(b, s); }
                catch (Exception e) { _cookFail++; LogOnce("hullerr" + b.S.Name, "her " + b.S.Name + ": hull cooking error (" + e.Message + "), capsule kept"); b.HullTried = true; }
                n++;
            }
            _cookSw.Stop();
            _cookMs += (float)_cookSw.Elapsed.TotalMilliseconds; _cookFrames++;
            _statusDirty = true;
            if (_cookQueue.Count == 0 && !_cookLogged && _cookOk + _cookFail > 0)
            {
                _cookLogged = true;   // the first drain of a build; later re-cooks (softness moved) stay quiet unless one fails
                Plugin.Log.LogInfo("PhysicsHands: her hulls cooked: " + _cookOk + " ok, " + _cookFail + " failed, " + _cookMs.ToString("0.0") + " ms total over " + _cookFrames + " frames");
            }
        }

        // The hull of one segment: HerBody's skin points pulled in by the inset (toward the axis, or the centre for a bone-space hull,
        // never closer than 12 mm) so her soft zones give first, as the capsule radius always was. Inset is in world metres and the hull
        // lives at scale (rr, rl, rr), so it is divided by rr (her bone's scale now). Failure keeps the capsule.
        static void CookHull(SegBody b, HandsSettings s)
        {
            var g = b.S;
            float inset = HerBody.Inset(g, s.Softness);
            b.HullTried = true; b.CookedInset = inset;
            var src = g.HullPts; var tris = g.HullTris;
            if (src == null || tris == null || src.Length < 4 || tris.Length < 12) return;
            float rr = g.RollRef != null ? Mathf.Clamp(HerBody.RollScale(g), 0.25f, 4f) : 1f;
            float ins = inset / rr, floor = 0.012f / rr;
            bool bone = g.Space == 1;
            Vector3 centre = Vector3.zero;
            if (bone) { for (int i = 0; i < src.Length; i++) centre += src[i]; centre /= src.Length; }
            var pts = new Vector3[src.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                Vector3 ax = bone ? centre : new Vector3(0f, src[i].y, 0f);
                Vector3 rad = src[i] - ax;
                float r = rad.magnitude;
                pts[i] = r < 1e-5f ? src[i] : ax + rad * (Mathf.Max(floor, r - ins) / r);   // a pole on the axis stays there
            }
            string fail = CookInto(b, pts, tris);
            int cols = HerBody.HullCols, grid = pts.Length - 2;
            if (fail != null && grid > 2 * cols && grid % cols == 0)
            {
                // PhysX refuses a hull of more than 255 polygons: a rough 12 x 16 grid can split into ~380 triangles. Retry with every other ROW of
                // the grid (rows run along the axis, so the cross-section stays a 16-gon; the first and last rows are kept) and both poles, with
                // the triangle list rebuilt for the smaller grid (HerBody.MakeTris, the same winding).
                int rows = grid / cols;
                var pick = new List<int>();
                for (int r = 0; r < rows; r += 2) pick.Add(r);
                if (pick[pick.Count - 1] != rows - 1) pick[pick.Count - 1] = rows - 1;
                var sub = new Vector3[pick.Count * cols + 2];
                for (int k = 0; k < pick.Count; k++)
                    for (int c = 0; c < cols; c++) sub[k * cols + c] = pts[pick[k] * cols + c];
                sub[pick.Count * cols] = pts[grid]; sub[pick.Count * cols + 1] = pts[grid + 1];
                string f2 = CookInto(b, sub, HerBody.MakeTris(pick.Count, cols));
                if (f2 == null) { LogOnce("hullreduce" + g.Name, "her " + g.Name + ": convex hull cooking failed (" + fail + "), cooked every other row: " + pick.Count + " x " + cols + " + 2 of " + rows + " x " + cols + " + 2 points"); fail = null; }
            }
            if (fail != null)
            {
                _cookFail++;
                LogOnce("hullfail" + g.Name, "her " + g.Name + ": convex hull cooking failed (" + fail + "), " + (b.HullOk ? "the previous hull kept" : "capsule kept"));
                return;
            }
            _cookOk++;
            b.HullOk = true;
            EnsureHullMark(b);
            ApplyShape(b);
        }

        // Cooks pts / tris into the segment's MeshCollider (made on the first success). null when it worked, else why not; on failure the
        // previous hull, if any, stays.
        static string CookInto(SegBody b, Vector3[] pts, int[] tris)
        {
            for (int i = 0; i < tris.Length; i++) if (tris[i] < 0 || tris[i] >= pts.Length) return "triangle index out of range";
            var mesh = new Mesh { name = "SfPhysHull_" + b.S.Name };
            mesh.vertices = pts; mesh.triangles = tris; mesh.RecalculateBounds();
            var mc = b.Hull;
            bool added = mc == null;
            if (added)
            {
                mc = b.Go.AddComponent<MeshCollider>();
                mc.cookingOptions = MeshColliderCookingOptions.CookForFasterSimulation | MeshColliderCookingOptions.EnableMeshCleaning | MeshColliderCookingOptions.WeldColocatedVertices | MeshColliderCookingOptions.UseFastMidphase;
                mc.convex = true;
                mc.sharedMaterial = _herMat;
            }
            bool was = mc.enabled;
            mc.enabled = true;   // a disabled collider reports empty bounds: enabled while we check the cook
            string fail = null;
            try { mc.sharedMesh = mesh; } catch (Exception e) { fail = e.Message; }
            if (fail == null && (mc.sharedMesh == null || mesh.bounds.size.sqrMagnitude < 1e-8f || mc.bounds.size.sqrMagnitude < 1e-8f)) fail = "empty shape";
            if (fail != null)
            {
                UnityEngine.Object.Destroy(mesh);
                if (added) UnityEngine.Object.DestroyImmediate(mc);
                else { try { mc.sharedMesh = b.HullMesh; } catch (Exception) { } mc.enabled = was; }
                return fail;
            }
            if (added) { b.Hull = mc; _segOfCol[mc] = b; mc.enabled = false; }   // ApplyShape enables it when it is the one in use
            else mc.enabled = was;
            var old = b.HullMesh;
            b.HullMesh = mesh;
            if (old != null) UnityEngine.Object.Destroy(old);
            return null;
        }

        // Which of the two colliders is on: the hull when it is wanted (HullColliders) and cooked, else the capsule. Switching never
        // re-cooks. The segment object's scale belongs to the shape (hull: SegPose's scale; capsule: one), so it is written here too.
        static void ApplyShape(SegBody b)
        {
            bool use = _hullWant && b.HullOk && b.Hull != null;
            if (use == b.UsingHull) { if (use) EnsureHullMark(b); return; }
            b.UsingHull = use;
            b.Cap.enabled = !use;
            if (b.Hull != null) b.Hull.enabled = use;
            b.H = -1f;   // StepHer writes the capsule's size again
            Vector3 p, sc; Quaternion q;
            if (use && HerBody.SegPose(b.S, out p, out q, out sc)) { b.T.localScale = sc; b.ScaleWritten = sc; }
            else { b.T.localScale = Vector3.one; b.ScaleWritten = use ? Vector3.zero : Vector3.one; }
            RefreshMarks(b);
            _statusDirty = true;
        }

        static bool ScaleMoved(Vector3 a, Vector3 w)
        {
            return Mathf.Abs(a.x - w.x) > 0.01f * w.x || Mathf.Abs(a.y - w.y) > 0.01f * w.y || Mathf.Abs(a.z - w.z) > 0.01f * w.z;
        }

        // Her live bones -> the kinematic shapes (MovePosition/MoveRotation so a moving hand feels her motion). The poses are sampled once
        // per rendered frame (her bones only change then) and extrapolated to the end of each fixed step, the way the palms follow the
        // controllers (Track): chasing the stale pose made every segment jump at the first step of a frame and rest at the others
        // (velocity alternating 2x / 0), which showed up as hundreds of contact starts per 10 s on a hand that merely rested on her.
        static void StepHer()
        {
            if (!_herBuilt) return;
            float fdt = Time.fixedDeltaTime;
            bool sample = Time.frameCount != _herSampleFrame;
            _herSampleFrame = Time.frameCount;
            // Inside FixedUpdate Time.fixedTime is already the time this step simulates TO (it counts 0.02 s at the first step), so the end of the
            // step is fixedTime itself, not fixedTime + fdt. (Outside the fixed loop it is the end of the last step, so the visual's
            // (Time.time - fixedTime) / fdt in UpdateHand is how far past that state the frame falls, in [0, 1): consistent with this.)
            float tEnd = Time.fixedTime;
            for (int i = 0; i < _bodies.Count; i++)
            {
                var b = _bodies[i];
                var g = b.S;
                HerBody.PublishPhys(g, fdt);
                if (g.A == null || (!g.Sphere && g.B == null) || b.Go == null) { HerBody.MarkDirty(); continue; }
                Vector3 p, sc; Quaternion q;
                if (!HerBody.SegPose(g, out p, out q, out sc)) continue;
                if (sample)
                {
                    if (g.Trk.Ok && (p - g.Trk.P).sqrMagnitude > 0.25f) { g.Trk.Reset(); b.Placed = false; }   // a cut: no velocity across it
                    g.Trk.Sample(p, q, _frameT, g.Trk.Ok ? _frameT - g.Trk.T : 0f, HerMaxV);
                }
                Vector3 tp = p; Quaternion tq = q;
                if (g.Trk.Ok) { tp = g.Trk.At(tEnd, MaxLead); tq = g.Trk.QAt(tEnd, MaxLead); }

                float ratio = sc.x;
                if (Mathf.Abs(ratio - b.Ratio) > 0.01f) b.Ratio = ratio;   // her bones were scaled: the skin got fatter or thinner
                if (b.UsingHull)
                {
                    if (ScaleMoved(sc, b.ScaleWritten)) { b.T.localScale = sc; b.ScaleWritten = sc; }   // the markers inherit it
                }
                else
                {
                    // the capsule lives at scale one: radius from the bone's scale, length from the live A -> B, centred along +Y from the joint
                    float len = g.Sphere ? 0f : sc.y * g.Len0;
                    float r = b.RBase * b.Ratio;
                    float h = Mathf.Max(2f * r, len + 2f * r);
                    // the head (bone space) is a sphere around its skin's centre, not around the neck joint where its frame starts
                    Vector3 ctr = g.Space == 1 ? g.FrameCenter * sc.x : new Vector3(0f, len * 0.5f, 0f);
                    // no hull points were collected (FrameCenter zero) but the sphere has a centre in A's space: put it there (the same point A.TransformPoint(LocalCenter) as SegPose's sphere branch)
                    if (g.Space == 1 && g.FrameCenter == Vector3.zero && g.LocalCenter != Vector3.zero) ctr = g.LocalCenter * Mathf.Abs(g.A.lossyScale.x);   // the frame is A's rotation at A's position (its scale rr is on the object for a hull only)
                    if (Mathf.Abs(r - b.Cap.radius) > 1e-4f || Mathf.Abs(h - b.H) > 1e-3f || !((ctr - b.Center).sqrMagnitude < 1e-6f))
                    {
                        b.Cap.radius = r; b.Cap.height = h; b.Cap.center = ctr; b.H = h; b.Center = ctr;
                        PlaceCapMark(b);
                    }
                }
                if (!b.Placed || (tp - b.LastPos).sqrMagnitude > 0.25f)
                {
                    b.T.SetPositionAndRotation(tp, tq);
                    b.Rb.position = tp; b.Rb.rotation = tq;
                    b.Placed = true;
                }
                else { b.Rb.MovePosition(tp); b.Rb.MoveRotation(tq); }
                b.LastPos = tp;
            }
        }

        // ================= debug markers =================

        static bool _dbgOn;
        static Material _matHand, _matHer;
        static readonly string[] ColorProps = { "_UnlitColor", "_BaseColor", "_Color" };

        static Material MarkMat(Color c)
        {
            var sh = Shader.Find("HDRP/Unlit");
            if (sh == null) sh = Shader.Find("HDRP/Lit");
            if (sh == null) return null;
            var m = new Material(sh) { name = Pfx + "_debug" };
            for (int i = 0; i < ColorProps.Length; i++) if (m.HasProperty(ColorProps[i])) m.SetColor(ColorProps[i], c);
            return m;
        }

        static GameObject NewMarker(Transform parent, PrimitiveType type, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = Pfx + "_debug";
            foreach (var comp in go.GetComponents<Component>())   // markers must never be touched or touch anything
                if (comp != null && comp.GetType().Name.EndsWith("Collider", StringComparison.Ordinal)) UnityEngine.Object.DestroyImmediate(comp);
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<Renderer>();
            if (r != null)
            {
                r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
                if (mat != null) r.sharedMaterial = mat;
            }
            return go;
        }

        static void AddMarkers(Hand h)
        {
            if (_matHand == null) _matHand = MarkMat(new Color(0.1f, 0.7f, 1f, 1f));
            RemoveMarkers(h);
            float u = h.Spec.Scale;
            for (int i = 0; i < h.Spec.Cols.Count && i < h.ColT.Count; i++)
            {
                var sh = h.Spec.Cols[i];
                GameObject m;
                if (sh.Box) { m = NewMarker(h.ColT[i], PrimitiveType.Cube, _matHand); m.transform.localScale = sh.Size / u; }
                else
                {
                    float len = (sh.B - sh.A).magnitude * u;
                    m = NewMarker(h.ColT[i], PrimitiveType.Capsule, _matHand);
                    m.transform.localScale = new Vector3(2f * sh.R, (len + 2f * sh.R) * 0.5f, 2f * sh.R) / u;
                }
                h.Marks.Add(m);
            }
        }

        static void RemoveMarkers(Hand h)
        {
            foreach (var m in h.Marks) if (m != null) UnityEngine.Object.Destroy(m);
            h.Marks.Clear();
        }

        // Her markers (orange): the capsule's primitive, and for a hull segment a child with the hull's own mesh (it inherits the segment's
        // scale like the collider does); only the shape that is in use is shown.
        static void AddHerMarkers()
        {
            if (_matHer == null) _matHer = MarkMat(new Color(1f, 0.5f, 0.1f, 1f));
            foreach (var b in _bodies)
            {
                if (b.Go == null) continue;
                if (b.Mark == null) { b.Mark = NewMarker(b.T, PrimitiveType.Capsule, _matHer); PlaceCapMark(b); }
                EnsureHullMark(b);
                RefreshMarks(b);
            }
        }

        static void PlaceCapMark(SegBody b)
        {
            if (b.Mark == null) return;
            float r = b.Cap.radius, h = Mathf.Max(2f * r, b.Cap.height);
            b.Mark.transform.localScale = new Vector3(2f * r, h * 0.5f, 2f * r);
            b.Mark.transform.localPosition = b.Cap.center;
        }

        static void EnsureHullMark(SegBody b)
        {
            if (!_dbgOn || b.Go == null || b.HullMesh == null) return;
            if (_matHer == null) _matHer = MarkMat(new Color(1f, 0.5f, 0.1f, 1f));
            MeshFilter mf;
            if (b.HMark == null)
            {
                b.HMark = new GameObject(Pfx + "_debug");
                b.HMark.transform.SetParent(b.T, false);
                mf = b.HMark.AddComponent<MeshFilter>();
                var mr = b.HMark.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
                if (_matHer != null) mr.sharedMaterial = _matHer;
                b.HMark.SetActive(b.UsingHull);
            }
            else mf = b.HMark.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != b.HullMesh) mf.sharedMesh = b.HullMesh;
        }

        static void RefreshMarks(SegBody b)
        {
            if (b.Mark != null) b.Mark.SetActive(!b.UsingHull);
            if (b.HMark != null) b.HMark.SetActive(b.UsingHull);
        }

        static void RemoveHerMarkers()
        {
            foreach (var b in _bodies)
            {
                if (b.Mark != null) { UnityEngine.Object.Destroy(b.Mark); b.Mark = null; }
                if (b.HMark != null) { UnityEngine.Object.Destroy(b.HMark); b.HMark = null; }
            }
        }

        static void SetDebug(bool on)
        {
            if (on == _dbgOn) return;
            _dbgOn = on;
            if (on)
            {
                if (_left != null) AddMarkers(_left);
                if (_right != null) AddMarkers(_right);
                AddHerMarkers();
            }
            else
            {
                if (_left != null) RemoveMarkers(_left);
                if (_right != null) RemoveMarkers(_right);
                RemoveHerMarkers();
                if (_matHand != null) UnityEngine.Object.Destroy(_matHand);
                if (_matHer != null) UnityEngine.Object.Destroy(_matHer);
                _matHand = _matHer = null;
            }
        }

        // ================= contacts and teleports (log) =================

        static readonly HashSet<string> _contacts = new HashSet<string>();
        static int _enters, _tpFar, _tpJump, _tpNan;
        static float _nextSummary;
        // frame bookkeeping for the follow: the time of the last rendered frame this ran in and its length, fixed steps since then,
        // and the 10 s statistics of steps per frame and of rig shifts (SummariseFcap)
        static float _frameT = -1f, _frameDt;
        static int _stepsNow, _stepSum, _stepFrames, _stepMax, _rigShifts;

        // Enter / Stay from the probes on the palm and the finger rigidbodies: first contacts go to the log, every call stamps the touch time.
        internal static void NoteContact(bool left, Collision c, bool enter)
        {
            try
            {
                var col = c.collider;
                if (ReferenceEquals(col, null) || col == null) return;
                SegBody b;
                if (!_segOfCol.TryGetValue(col, out b)) return;
                var g = b.S;
                float t = Time.fixedTime;
                // 0.7.9 reactions: a resting hand chatters enter/exit; only a contact after >= 0.15 s of none from THIS hand is an arrival
                bool fresh = t - (left ? g.TouchTL : g.TouchTR) > 0.15f;
                g.TouchT = t;
                if (left) g.TouchTL = t; else g.TouchTR = t;
                _handTouchT[left ? 0 : 1] = t;
                HerBody.AddImpulse(g, left, c);
                if (!enter) return;
                if (fresh && c.contactCount > 0)
                {
                    var cp = c.GetContact(0);                     // allocation-free
                    Vector3 n = cp.normal; var mine = cp.thisCollider == c.collider ? cp.otherCollider : cp.thisCollider;
                    if (mine != null && Vector3.Dot(n, mine.bounds.center - cp.point) < 0f) n = -n;   // n points at the hand, as AddImpulse
                    Vector3 rv = c.relativeVelocity;
                    g.HitT = t; g.HitVn = Mathf.Abs(Vector3.Dot(rv, n)); g.HitSpeed = rv.magnitude; g.HitPt = cp.point; g.HitDir = -n; g.HitLeft = left;
                }
                _enters++;
                if (_contacts.Add((left ? "L:" : "R:") + g.Name))
                    Plugin.Log.LogInfo("PhysicsHands: first contact " + (left ? "left" : "right") + " hand with her " + g.Name + " (" + c.relativeVelocity.magnitude.ToString("0.00") + " m/s, " + c.contactCount + " points)");
            }
            catch (Exception) { }
        }

        static void Teleport(Hand h, Vector3 p, Quaternion q)
        {
            var rb = h.Rb;
            h.ErrPrevOk = false;
            if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            rb.position = p; rb.rotation = q;
            h.PRoot.SetPositionAndRotation(p, q);
            for (int i = 0; i < h.Rig.Count; i++)   // the fingers ride along on the transform hierarchy; their motion is dropped
            {
                var frb = h.Rig[i].Rb;
                if (frb != null && !frb.isKinematic) { frb.velocity = Vector3.zero; frb.angularVelocity = Vector3.zero; }
            }
            if (h.Rig.Count > 0) Physics.SyncTransforms();
            h.CurValid = false;
            h.FarTime = 0f;
            h.Trk.Reset(); h.OffValid = false; h.VisHave = false;   // the sampled target and the offset history belong to where the hand was
        }

        // The game moved the rig the controllers hang under (a POV or shot cut): the target jumped, the hand did not. Carry the hand along
        // with it instead of letting it race across the gap through her: it keeps its displacement from its target (how far a blocked
        // hand stands from the controller), rotated by the same turn as the target; its velocity is rotated too, and the fingers ride
        // along on the transform hierarchy. The turn is taken from the OLD sampled rotation, so this runs before the new sample. Not a
        // teleport: nothing is dropped, a hand held against her stays held.
        static void ShiftHand(Hand h, Vector3 tp, Quaternion tq)
        {
            var rb = h.Rb;
            h.ErrPrevOk = false;
            Quaternion dq = tq * Quaternion.Inverse(h.Trk.Q);
            if (!Finite(dq)) { Teleport(h, tp, tq); return; }
            Vector3 rel = h.HaveFixed ? rb.position - h.FixedPos : Vector3.zero;   // from the target the last step aimed at, not the lead-free sample
            if (!Finite(rel) || rel.sqrMagnitude > 0.25f) rel = Vector3.zero;
            Vector3 np = tp + dq * rel;
            Quaternion nq = dq * rb.rotation;
            rb.position = np; rb.rotation = nq;
            h.PRoot.SetPositionAndRotation(np, nq);
            rb.velocity = dq * rb.velocity; rb.angularVelocity = dq * rb.angularVelocity;
            for (int i = 0; i < h.Rig.Count; i++)
            {
                var frb = h.Rig[i].Rb;
                if (frb != null && !frb.isKinematic) { frb.velocity = dq * frb.velocity; frb.angularVelocity = dq * frb.angularVelocity; }
            }
            if (h.Rig.Count > 0) Physics.SyncTransforms();
            h.FixedPos = tp; h.FixedRot = tq; h.HaveFixed = true; h.LastTarget = tp; h.HaveLast = true;
            h.CurValid = false; h.FarTime = 0f;
            h.Trk.Reset();
            h.Off0P = h.Off1P = np - tp; h.Off0Q = h.Off1Q = nq * Quaternion.Inverse(tq); h.OffValid = true; h.OffSeen = h.StepsDone;
            h.VisHave = false;
        }

        // The body's displacement from the target it aimed at, at the end of the fixed step that just ran: the rigidbody now (interpolation
        // is None, so its pose is the last step's result) against the SAME extrapolated target that step used (FixedPos / FixedRot, not the
        // raw controller). Called before a step replaces the target and from the rendered frame, so every step's offset is seen; the last
        // two are kept for the visual to interpolate between.
        static void CaptureOffset(Hand h)
        {
            if (h.StepsDone == h.OffSeen) return;
            h.OffSeen = h.StepsDone;
            if (h.Kin || !h.HaveFixed || h.Rb == null) { h.OffValid = false; return; }
            Vector3 dp = h.Rb.position - h.FixedPos;
            Quaternion dq = h.Rb.rotation * Quaternion.Inverse(h.FixedRot);
            if (!Finite(dp) || !Finite(dq) || dp.sqrMagnitude >= 0.5f * 0.5f) { h.OffValid = false; return; }
            if (h.OffValid) { h.Off0P = h.Off1P; h.Off0Q = h.Off1Q; } else { h.Off0P = dp; h.Off0Q = dq; }
            h.Off1P = dp; h.Off1Q = dq; h.OffValid = true;
        }

        // ================= per fixed step =================

        // Runner.FixedUpdate: the palm follows the target by a force-capped pull and a PD torque, the fingers get their joint
        // targets; her capsules follow her bones. Before the physics step. (The pushes the hands give her: HerBody.PushOn.)
        public static void FixedStep()
        {
            if (!Active && !_herBuilt) return;
            try
            {
                _stepsNow++;
                float dt = Time.fixedDeltaTime;
                if (_s != null && dt > 0f)
                {
                    if (_left != null) StepHand(_left, VrHands.LeftController, _s.LeftPos, _s.LeftRot, dt);
                    if (_right != null) StepHand(_right, VrHands.RightController, _s.RightPos, _s.RightRot, dt);
                }
                StepHer();
                float now = Time.unscaledTime;
                if (now >= _nextSummary)
                {
                    _nextSummary = now + 10f;
                    if (_tpFar + _tpJump + _tpNan > 0 || _enters > 0)
                        Plugin.Log.LogInfo("PhysicsHands: last 10 s: " + _enters + " contact starts, teleports " + (_tpFar + _tpJump + _tpNan) + " (too far " + _tpFar + ", target jumped " + _tpJump + ", NaN " + _tpNan + ")");
                    _enters = _tpFar = _tpJump = _tpNan = 0;
                    SummariseFcap(_left, "left"); SummariseFcap(_right, "right");
                    _stepSum = _stepFrames = _stepMax = 0; _rigShifts = 0;
                }
            }
            catch (Exception e)
            {
                LogOnce("fixederr" + e.GetType().Name, "FixedStep error: " + e);
            }
        }

        // The 10 s line per palm. "At the cap": the share of fixed steps the pull wanted more than the force cap; split into steps in free
        // air (nothing of hers touched for 0.3 s) and steps touching her, each as the share of its own steps (free air should be near 0:
        // a palm saturating in free air was the 0.7.7 bug). Gap: how far the palm stood from its target on the steps at the cap.
        // Steps/frame and rig shifts are global; the jitter is the frame-to-frame change of the offset the visual adds to the controller.
        static void SummariseFcap(Hand h, string side)
        {
            if (h == null || h.CapN == 0) return;
            var sb = new StringBuilder("PhysicsHands: last 10 s " + side + " palm: force cap avg " + (h.CapSum / h.CapN).ToString("0.0") + " N, resist max " + h.ResistMax.ToString("0.00")
                               + ", at the cap " + (100f * h.CapSat / h.CapN).ToString("0") + "% (free air " + Pct(h.CapSatFree, h.CapFreeN) + " of " + h.CapFreeN + " steps, touching " + Pct(h.CapSatTouch, h.CapTouchN) + " of " + h.CapTouchN + ")"
                               + (h.CapSat > 0 ? ", gap at the cap avg " + (100f * h.GapAtCapSum / h.CapSat).ToString("0.0") + " cm" : ""));
            if (_stepFrames > 0) sb.Append("; fixed steps/frame avg ").Append(((float)_stepSum / _stepFrames).ToString("0.0")).Append(" max ").Append(_stepMax);
            sb.Append("; rig shifts ").Append(_rigShifts);
            // 0.8.0: the offset of the physics body from the controller, RAW (what the follow loop leaves) against SHOWN (what the gate lets
            // through). The raw free-air max says whether the loop is quiet; the shown numbers are what the user sees.
            if (h.OffN > 0)
            {
                float n = h.OffN;
                sb.Append("; drawn offset raw avg ").Append((h.RawMmSum / n).ToString("0.0")).Append(" mm / ").Append((h.RawDegSum / n).ToString("0.0")).Append(" deg (free air ").Append(h.FreeN)
                  .Append(" frames: max ").Append(h.FreeMaxMm.ToString("0.0")).Append(" mm / ").Append(h.FreeMaxDeg.ToString("0.0")).Append(" deg), shown avg ")
                  .Append((h.ShownMmSum / n).ToString("0.0")).Append(" mm / ").Append((h.ShownDegSum / n).ToString("0.0")).Append(" deg");
                if (h.JitN > 0)
                    sb.Append("; jitter raw avg ").Append((h.JitRawSum / h.JitN).ToString("0.0")).Append(" mm max ").Append(h.JitRawMax.ToString("0.0")).Append(" mm / ").Append((h.JitRawQSum / h.JitN).ToString("0.00")).Append(" deg max ").Append(h.JitRawQMax.ToString("0.00"))
                      .Append(" deg, shown avg ").Append((h.JitSum / h.JitN).ToString("0.0")).Append(" mm max ").Append(h.JitMax.ToString("0.0")).Append(" mm / ").Append((h.JitQSum / h.JitN).ToString("0.00")).Append(" deg max ").Append(h.JitQMax.ToString("0.00")).Append(" deg");
            }
            if (h.Fan[0] != null || h.Fan[1] != null)
                sb.Append("; fingers: conform held idx/mid/ring/pinky/thumb ").Append(h.FHeldN[1]).Append('/').Append(h.FHeldN[2]).Append('/').Append(h.FHeldN[3]).Append('/').Append(h.FHeldN[4]).Append('/').Append(h.FHeldN[0])
                  .Append(" frames, avg contact curl ").Append(h.FContactN > 0 ? (h.FContactSum / h.FContactN).ToString("0.00") : "-")
                  .Append(", give used avg ").Append(h.FGiveN > 0 ? (h.FGiveSum / h.FGiveN).ToString("0.00") : "-").Append(", probe flips ").Append(h.FFlipN);
            sb.Append("; follow v err avg ").Append((h.VErrSum / h.CapN).ToString("0.00")).Append(" m/s max ").Append(h.VErrMax.ToString("0.00")).Append(", flips ").Append(Pct(h.VFlipN, h.CapN))
              .Append("; spin err avg ").Append((h.WErrSum / h.CapN).ToString("0.0")).Append(" rad/s max ").Append(h.WErrMax.ToString("0.0")).Append(", flips ").Append(Pct(h.WFlipN, h.CapN))
              .Append(" (velocity follow ").Append(FollowGain.ToString("0.0")).Append("/").Append(TurnGain.ToString("0.0")).Append(")");
            if (!h.RigOn) sb.Append("; fingers ").Append(_s != null && !_s.JointedFingers ? "kinematic (switched off)" : h.RigFailed ? "kinematic (fallback)" : "kinematic");
            Plugin.Log.LogInfo(sb.ToString());
            h.CapSum = 0f; h.CapN = 0; h.CapSat = 0; h.ResistMax = 0f;
            h.CapFreeN = h.CapTouchN = h.CapSatFree = h.CapSatTouch = 0; h.GapAtCapSum = 0f;
            h.JitSum = 0f; h.JitMax = 0f; h.JitN = 0; h.JitQSum = 0f; h.JitQMax = 0f;
            h.JitRawSum = h.JitRawMax = h.JitRawQSum = h.JitRawQMax = 0f;
            h.OffN = h.FreeN = 0; h.RawMmSum = h.RawDegSum = h.ShownMmSum = h.ShownDegSum = h.FreeMaxMm = h.FreeMaxDeg = 0f;
            for (int f = 0; f < 5; f++) h.FHeldN[f] = 0;
            h.FContactN = h.FGiveN = h.FFlipN = 0; h.FContactSum = h.FGiveSum = 0f;
            h.VErrSum = h.VErrMax = h.WErrSum = h.WErrMax = 0f; h.VFlipN = h.WFlipN = 0;
        }

        static string Pct(int n, int of) { return of > 0 ? (100f * n / of).ToString("0") + "%" : "-"; }

        // Follow, per fixed step. The target is NOT the controller's transform: the controllers only change once per rendered frame while
        // physics runs 0, 1 or several steps in one, so a stale target made the old one-step deadbeat accelerate on the step that saw the
        // new pose and brake on the next (bang-bang: the pull sat at its force cap in free air). Instead UpdateHand samples the target
        // once per frame with its velocity (Track); here it is extrapolated to the end of this step, the palm is asked for that velocity
        // (feed-forward) plus a correction closing the gap at GapGain per second, and the force is the one that gets there in one step,
        // capped as before. In free air the velocity error stays near zero and the cap is not reached.
        static void StepHand(Hand h, Transform ctl, Vector3 off, Vector3 rot, float dt)
        {
            var rb = h.Rb;
            if (h.PGo == null || rb == null) return;
            if (ctl == null)
            {
                h.HaveFixed = false; h.HaveLast = false; h.OffValid = false; h.Trk.Reset();
                if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                return;
            }
            CaptureOffset(h);   // what the previous step did, against the target it aimed at (before FixedPos is replaced below)
            Vector3 tp; Quaternion tq;
            if (h.Trk.Ok) { tp = h.Trk.At(Time.fixedTime, MaxLead); tq = h.Trk.QAt(Time.fixedTime, MaxLead); }   // fixedTime = the end of this step (see StepHer)
            else Target(ctl, off, rot, h.Spec.Align, out tp, out tq);
            if (!Finite(tp) || !Finite(tq))
            {
                _tpNan++;
                h.HaveFixed = false; h.OffValid = false; h.Trk.Reset();
                if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                return;
            }
            h.FixedPos = tp; h.FixedRot = tq; h.HaveFixed = true;
            h.StepsDone++;
            if (h.Kin)
            {
                rb.MovePosition(tp); rb.MoveRotation(tq);
                h.LastTarget = tp; h.HaveLast = true;
                return;
            }
            Vector3 pos = rb.position; Quaternion rq = rb.rotation;
            if (!Finite(pos) || !Finite(rq)) { _tpNan++; Teleport(h, tp, tq); h.LastTarget = tp; h.HaveLast = true; return; }
            if (h.HaveLast && (tp - h.LastTarget).sqrMagnitude > 1f) { _tpJump++; Teleport(h, tp, tq); h.LastTarget = tp; return; }
            h.LastTarget = tp; h.HaveLast = true;
            Vector3 gap = tp - pos;
            if (gap.sqrMagnitude > 0.35f * 0.35f)
            {
                h.FarTime += dt;
                if (h.FarTime > 0.25f) { _tpFar++; Teleport(h, tp, tq); return; }
            }
            else h.FarTime = 0f;

            // Compliant follow at the VELOCITY level. Until 0.7.8 a force sized for the whole hand (palm + fingers, 0.70 kg) and a torque
            // sized for its inertia (fingers by the parallel-axis term) were applied to the palm alone: the palm got up to 2x the intended
            // velocity change and 7-14x the intended spin before the joints shared it with the fingers, so both loops oscillated at the step
            // rate at their caps (the logs: "at the cap" 87-99 % in free air, the drawn hand jittering by mm and degrees). Now a velocity
            // change of FollowGain/TurnGain (< 1) of the error is applied to EVERY body of the hand as one rigid motion: the realised gain is
            // that constant whatever the joints do, and the loop cannot overshoot. Translation target: the sampled target velocity plus the
            // gap closed at GapGain per second (at most MaxGapV). The per-step change is capped at fcap*dt/mass, i.e. the old force cap
            // (60 N, down to 12 N as her skin under the hand resists: SoftZones.LeftResist/RightResist), so a pressed hand still stops.
            float resist = Mathf.Clamp01(h.Left ? SoftZones.LeftResist : SoftZones.RightResist);
            float fcap = Mathf.Lerp(60f, 12f, resist);
            Vector3 vGap = gap * GapGain;
            if (vGap.sqrMagnitude > MaxGapV * MaxGapV) vGap = vGap.normalized * MaxGapV;
            Vector3 vDes = h.Trk.V + vGap;
            if (vDes.sqrMagnitude > 36f) vDes = vDes.normalized * 6f;
            Vector3 vErr = vDes - rb.velocity;
            Vector3 dv = vErr * FollowGain;
            float dvCap = fcap * dt / Mathf.Max(h.TotalMass, 0.05f);   // 60 N -> 1.7 m/s per step at 0.70 kg, 12 N -> 0.34
            float dvm = dv.magnitude;
            bool capped = dvm > dvCap;
            if (capped) dv *= dvCap / dvm;
            bool touching = Time.fixedTime - _handTouchT[h.Left ? 0 : 1] < 0.3f;   // same window as FingerStep
            h.CapSum += fcap; h.CapN++;
            if (touching) h.CapTouchN++; else h.CapFreeN++;
            if (capped) { h.CapSat++; h.GapAtCapSum += gap.magnitude; if (touching) h.CapSatTouch++; else h.CapSatFree++; }
            if (resist > h.ResistMax) h.ResistMax = resist;

            // Rotation: the same loop. Target spin = the controller's (Trk.W, feed-forward) plus the angle closed at RotGapGain per second;
            // the per-step change is capped by TorqueCap (TouchTorqueCap while she is touched) through the hand's inertia about the axis of
            // the change (palm + fingers).
            Quaternion dq = tq * Quaternion.Inverse(rq);
            if (dq.w < 0f) dq = new Quaternion(-dq.x, -dq.y, -dq.z, -dq.w);
            float ang; Vector3 axis;
            dq.ToAngleAxis(out ang, out axis);
            if (ang > 180f) ang -= 360f;
            Vector3 wGap = Finite(axis) ? axis * (ang * Mathf.Deg2Rad * RotGapGain) : Vector3.zero;
            if (wGap.sqrMagnitude > MaxGapW * MaxGapW) wGap = wGap.normalized * MaxGapW;
            Vector3 wErr = h.Trk.W + wGap - rb.angularVelocity;
            Vector3 dw = wErr * TurnGain;
            Vector3 com = rb.worldCenterOfMass;
            float dwm = dw.magnitude;
            if (dwm > 1e-6f)
            {
                Vector3 n = dw / dwm;
                Quaternion ir = rq * rb.inertiaTensorRotation;
                Vector3 nl = Quaternion.Inverse(ir) * n, it = rb.inertiaTensor;
                float I = nl.x * nl.x * it.x + nl.y * nl.y * it.y + nl.z * nl.z * it.z;
                for (int i = 0; i < h.Rig.Count; i++)
                {
                    var g = h.Rig[i];
                    if (g.Rb == null || g.Rb.isKinematic) continue;
                    Vector3 r = g.Rb.worldCenterOfMass - com;
                    float rn = Vector3.Dot(r, n);
                    I += g.Rb.mass * (r.sqrMagnitude - rn * rn);
                }
                float dwCap = (touching ? TouchTorqueCap : TorqueCap) * dt / Mathf.Max(I, 1e-5f);
                if (dwm > dwCap) dw *= dwCap / dwm;
            }
            if (!Finite(dv)) dv = Vector3.zero;
            if (!Finite(dw)) dw = Vector3.zero;
            rb.AddForce(dv, ForceMode.VelocityChange);
            rb.AddTorque(dw, ForceMode.VelocityChange);
            for (int i = 0; i < h.Rig.Count; i++)   // the fingers get the same rigid motion: nothing left for the joints to redistribute
            {
                var frb = h.Rig[i].Rb;
                if (frb == null || frb.isKinematic) continue;
                frb.AddForce(dv + Vector3.Cross(dw, frb.worldCenterOfMass - com), ForceMode.VelocityChange);
                frb.AddTorque(dw, ForceMode.VelocityChange);
            }
            float ve = vErr.magnitude, we = wErr.magnitude;
            h.VErrSum += ve; if (ve > h.VErrMax) h.VErrMax = ve;
            h.WErrSum += we; if (we > h.WErrMax) h.WErrMax = we;
            if (h.ErrPrevOk)
            {
                if (ve > 0.25f && h.VErrPrev.magnitude > 0.25f && Vector3.Dot(vErr, h.VErrPrev) < 0f) h.VFlipN++;
                if (we > 2f && h.WErrPrev.magnitude > 2f && Vector3.Dot(wErr, h.WErrPrev) < 0f) h.WFlipN++;
            }
            h.VErrPrev = vErr; h.WErrPrev = wErr; h.ErrPrevOk = true;

            if (h.RigOn) FingerStep(h, dt);
        }

        // ================= per frame (the apply step) =================

        static bool _statusDirty, _fadeLogged;
        static string _gripSeen = "";

        static void SetDisp(bool left, Vector3 dp, Quaternion dq)
        {
            if (left) { LeftDp = dp; LeftDq = dq; } else { RightDp = dp; RightDq = dq; }
        }

        public static void Update(Character her, HandsSettings s)
        {
            SetDisp(true, Vector3.zero, Quaternion.identity);    // set again by UpdateHand below when the hand is dynamic
            SetDisp(false, Vector3.zero, Quaternion.identity);
            LeftFingersValid = RightFingersValid = false;       // set again by UpdateHand for a tracked, built hand
            if (s == null || !s.On)
            {
                if (_taken) Release();
                else if (Active || Status.Length > 0) { Active = false; Status = ""; }
                return;
            }
            try { Step(her, s); }
            catch (Exception e)
            {
                Plugin.Log.LogError("PhysicsHands failed, off for 5 s: " + e);
                Release();
                _retryAt = Time.unscaledTime + 5f;
                SetStatus("off: error " + e.GetType().Name + ": " + e.Message);
            }
        }

        static void Step(Character her, HandsSettings s)
        {
            _s = s;
            float now = Time.unscaledTime, dt = Time.deltaTime;
            // the rendered frame: its time and length (the targets are sampled against them) and how many fixed steps ran since the last one
            float tNow = Time.time;
            _frameDt = _frameT >= 0f ? tNow - _frameT : dt;
            _frameT = tNow;
            _stepSum += _stepsNow; if (_stepsNow > _stepMax) _stepMax = _stepsNow; _stepFrames++; _stepsNow = 0;
            if (_frameDt > 0.5f || _frameDt < 0f) ResetTracks();   // a pause, a load or a long hitch: no velocity across it
            if (now < _retryAt) return;
            Grip.Update();   // idempotent within a frame: the integrator calls it earlier in the apply step
            if (!_layersApplied)
            {
                string why = Prepare();
                if (why != null) { SetStatus("off: " + why); _retryAt = now + 5f; return; }
            }
            _taken = true;
            if (_handMat != null && Mathf.Abs(s.Friction - _matF) > 0.01f) SetFriction(Mathf.Clamp01(s.Friction));
            if (!_brSub) { Application.onBeforeRender += BeforeRender; _brSub = true; }

            var cl = VrHands.LeftController; var cr = VrHands.RightController;
            if (cl == null) cl = null;   // a destroyed controller reads as null: make it really null
            if (cr == null) cr = null;
            SetDebug(s.ShowColliders);

            // hands: built when their controller is known, rebuilt when the mesh choice changed or a part was destroyed
            ManageHand(ref _left, true, cl, s, now);
            ManageHand(ref _right, false, cr, s, now);
            if (_left == null && _right == null)
            {
                if (cl == null && cr == null) SetStatus("off: no VR controller found (VrHands has none yet)");
                else if (_statusDirty) SetStatus("off: couldn't build the hands (see the log)");
                _statusDirty = false;
                Active = false;
                if (_herBuilt) DestroyHer();
                return;
            }

            // her body
            // (the geometry is HerBody's: built by Body while Physics or the hands are on; the bodies follow its BuildVersion)
            if (s.Collide && her != null && her.Root != null && HerBody.Built)
            {
                if ((!_herBuilt && now >= _herNext) || (_herBuilt && _herVersion != HerBody.BuildVersion))
                {
                    try { BuildHerBodies(s); }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError("PhysicsHands: her colliders failed: " + e);
                        DestroyHer();
                        _herNote = "her: collider build failed";
                        _herNext = now + 10f;
                    }
                }
                if (_herBuilt && Mathf.Abs(s.Softness - _herSoftness) > 0.02f)
                {
                    _herSoftness = s.Softness;   // the soft zones' inset follows the softness setting: capsules at once, hulls re-cooked
                    foreach (var b in _bodies) b.RBase = Mathf.Max(0.012f, b.S.RSkin - HerBody.Inset(b.S, s.Softness));
                    QueueHulls(s);
                }
                if (_herBuilt)
                {
                    if (s.HullColliders != _hullWant)   // hulls <-> capsules by the setting, without cooking again
                    {
                        _hullWant = s.HullColliders;
                        QueueHulls(s);
                        foreach (var b in _bodies) ApplyShape(b);
                        _statusDirty = true;
                    }
                    CookStep(s);
                }
            }
            else if (_herBuilt) DestroyHer();
            if (HerBody.Built != _herBuiltSeen || !ReferenceEquals(HerBody.Note, _herNoteSeen)) { _herBuiltSeen = HerBody.Built; _herNoteSeen = HerBody.Note; _statusDirty = true; }

            // hands every frame
            if (_left != null) UpdateHand(_left, cl, s, dt, now);
            if (_right != null) UpdateHand(_right, cr, s, dt, now);
            if (_left != null && cl != null) HideController(_hideL, true, now);
            if (_right != null && cr != null) HideController(_hideR, false, now);
            if (_left == null) RestoreHide(_hideL);
            if (_right == null) RestoreHide(_hideR);

            if (Grip.Source != _gripSeen) { _gripSeen = Grip.Source; _statusDirty = true; }
            if (_statusDirty)
            {
                _statusDirty = false;
                SetStatus(OnStatus(s, her));
            }
        }

        static string OnStatus(HandsSettings s, Character her)
        {
            var sb = new StringBuilder("on: ");
            AppendHand(sb, _left, "L"); sb.Append(", "); AppendHand(sb, _right, "R");
            if (!s.Collide) sb.Append("; collisions off (visual only)");
            else if (_herBuilt)
            {
                int hulls = 0;
                foreach (var b in _bodies) if (b.UsingHull) hulls++;
                int cooking = _cookQueue.Count, plain = Mathf.Max(0, _bodies.Count - hulls - cooking);
                sb.Append("; her ").Append(_bodies.Count).Append(" colliders (").Append(hulls).Append(" hulls, ");
                if (cooking > 0) sb.Append(cooking).Append(" cooking, ");
                sb.Append(plain).Append(" capsules/spheres)");
            }
            else sb.Append("; ").Append(_herNote.Length > 0 ? _herNote : HerBody.Note.Length > 0 ? HerBody.Note : (her == null ? "her: not found" : "her: building"));
            sb.Append("; layers ").Append(_layerHand).Append('/').Append(_layerHer).Append("; grip ").Append(Grip.Source.Length > 0 ? Grip.Source : "none yet");
            return sb.ToString();
        }

        static void AppendHand(StringBuilder sb, Hand h, string side)
        {
            if (h == null) { sb.Append(side).Append(" none"); return; }
            sb.Append(side).Append(' ').Append(h.Spec.Visual).Append(h.Fallback ? " (fallback)" : "")
              .Append(h.RigOn ? ", jointed fingers" : _s != null && !_s.JointedFingers && h.CollideOn ? ", kinematic fingers (switched off)" : ", kinematic fingers");

        }

        static void ManageHand(ref Hand slot, bool left, Transform ctl, HandsSettings s, float now)
        {
            var h = slot;
            if (h != null && (h.PGo == null || h.VGo == null || h.BuiltMesh != s.Mesh))
            {
                DestroyHand(h); RestoreHide(left ? _hideL : _hideR);
                slot = h = null;
                RebuildCapsNow = true;
            }
            if (h == null)
            {
                if (ctl == null || now < (left ? _nextBuildL : _nextBuildR)) { if (RebuildCapsNow) { RebuildCaps(); RebuildCapsNow = false; } return; }
                slot = MakeHand(left, ctl, s, null);
                if (slot == null) { if (left) _nextBuildL = now + 5f; else _nextBuildR = now + 5f; }
                RebuildCapsNow = true;
            }
            else if (h.Fallback && s.Mesh == 0 && now >= h.NextUpgrade && ctl != null)
            {
                // the player wasn't there (or its hand couldn't be read) at first: look again now and then
                h.NextUpgrade = now + 10f;
                string why;
                HandSpec sp = null;
                try { sp = GameSpec(left, out why); } catch (Exception) { }
                if (sp != null)
                {
                    DestroyHand(h); RestoreHide(left ? _hideL : _hideR);
                    slot = MakeHand(left, ctl, s, sp);
                    RebuildCapsNow = true;
                }
            }
            if (RebuildCapsNow) { RebuildCaps(); RebuildCapsNow = false; }
        }
        static bool RebuildCapsNow;
        static float _nextBuildL, _nextBuildR;

        static void SetVisible(Hand h, bool on)
        {
            for (int i = 0; i < h.Rends.Count; i++)
            {
                var r = h.Rends[i];
                if (r != null && r.enabled != on) r.enabled = on;
            }
        }

        static void SetCollide(Hand h, bool on, Vector3 p, Quaternion q)
        {
            h.CollideOn = on;
            foreach (var c in h.Cols) if (c != null) c.enabled = on;
            var rb = h.Rb;
            if (on)
            {
                rb.isKinematic = false;
                Teleport(h, p, q);
                h.Kin = false;
                if (!h.RigFailed && _s != null && _s.JointedFingers) EnableRig(h);
            }
            else
            {
                DisableRig(h);   // visual only: kinematic fingers posed by the curl, like the palm
                rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
                h.Kin = true;
            }
        }

        static void UpdateHand(Hand h, Transform ctl, HandsSettings s, float dt, float now)
        {
            if (h.PGo == null || h.VGo == null) return;
            if (ctl == null) { SetVisible(h, false); h.Trk.Reset(); h.OffValid = false; h.VisHave = false; return; }
            Vector3 tp; Quaternion tq;
            Target(ctl, h.Left ? s.LeftPos : s.RightPos, h.Left ? s.LeftRot : s.RightRot, h.Spec.Align, out tp, out tq);
            if (!Finite(tp) || !Finite(tq)) return;
            if (h.CollideOn != s.Collide) SetCollide(h, s.Collide, tp, tq);
            if (h.JointWas != s.JointedFingers)   // the finger rig on or off at runtime
            {
                h.JointWas = s.JointedFingers;
                if (h.CollideOn && !h.Kin)
                {
                    if (s.JointedFingers) { if (!h.RigFailed) EnableRig(h); }
                    else DisableRig(h);
                }
                Plugin.Log.LogInfo("PhysicsHands: jointed fingers " + (s.JointedFingers ? "on" : "off") + " (" + (h.Left ? "left" : "right") + " hand)");
                _statusDirty = true;
            }

            // The target for the fixed steps: sampled once per rendered frame with its velocity (Track). A sample that jumped further than a
            // hand can move in the frame is the game moving the rig the controllers hang under: the hand is carried along (ShiftHand) before
            // the new sample replaces the old one it needs.
            if (h.Trk.Ok && !h.Kin)
            {
                float thr = JumpBase + JumpSpeed * _frameDt;
                if ((tp - h.Trk.P).sqrMagnitude > thr * thr) { ShiftHand(h, tp, tq); _rigShifts++; }
            }
            h.Trk.Sample(tp, tq, _frameT, _frameDt, MaxFf);

            // The visual: the fresh controller target composed with where the body stands relative to the target it aimed at. In free air
            // that offset is ~0 (1:1 with the controller, no added latency); blocked, it is the displacement. The offset is measured per
            // FIXED step but shown every RENDERED frame, and the two rates differ (50 Hz against 72-90 Hz): a frame after 0 steps shows the
            // same pose as the last one, a frame after 2 jumps by two steps, which reads as stutter. So the last two steps' offsets are kept
            // (CaptureOffset) and blended by how far into the current step the frame falls, as a Rigidbody with Interpolate would (the rigidbody
            // stays on None: the target it aims at is the one the offset is measured against).
            //
            // 0.8.0 (section 4): what is SHOWN of that offset is gated. Whatever the follow loop leaves in free air (Track's half-frame
            // velocity lag, PhysX solver noise, finger-drive transients) must not show, so the drawn hand is exactly the controller until the
            // hand touches her (weight 1 within 40 ms) or the offset is clearly more than noise (5-15 mm, 4-10 deg); it fades back over
            // 250 ms. The raw offset is still measured (the 10 s line reports both) and the rigidbody is untouched: this is only what is drawn.
            Vector3 vp = tp; Quaternion vq = tq;
            h.VisP = Vector3.zero; h.VisQ = Quaternion.identity; h.VisFrame = Time.frameCount;   // BeforeRender puts the same offset on the late controller pose
            if (s.Collide && !h.Kin)
            {
                CaptureOffset(h);
                if (h.OffValid)
                {
                    float fdt = Time.fixedDeltaTime;
                    float a = fdt > 0f ? Mathf.Clamp01((Time.time - Time.fixedTime) / fdt) : 1f;
                    Vector3 dp = Vector3.Lerp(h.Off0P, h.Off1P, a);
                    Quaternion dq = Quaternion.Slerp(h.Off0Q, h.Off1Q, a);
                    float rawMm = dp.magnitude * 1000f, rawDeg = Quaternion.Angle(Quaternion.identity, dq);
                    if (!Finite(dp) || !Finite(dq) || float.IsNaN(rawDeg)) { dp = Vector3.zero; dq = Quaternion.identity; rawMm = rawDeg = 0f; }
                    bool touchingHand = Time.fixedTime - _handTouchT[h.Left ? 0 : 1] < 0.3f;
                    if (s.DrawnOffsetFade)
                    {
                        float wRaw = touchingHand ? 1f : Mathf.Max(Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(GateMmLo, GateMmHi, dp.magnitude)), Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(GateDegLo, GateDegHi, rawDeg)));
                        float tau = wRaw > h.VisW ? GateAttack : GateRelease;
                        h.VisW += (wRaw - h.VisW) * (1f - Mathf.Exp(-Mathf.Min(dt, 0.1f) / tau));
                        if (!_fadeLogged)
                        {
                            _fadeLogged = true;
                            LogOnce("fade", "drawn offset fade on (" + (GateMmLo * 1000f).ToString("0") + "-" + (GateMmHi * 1000f).ToString("0") + " mm / " + GateDegLo.ToString("0") + "-" + GateDegHi.ToString("0")
                                            + " deg, attack " + (GateAttack * 1000f).ToString("0") + " ms, release " + (GateRelease * 1000f).ToString("0") + " ms)");
                        }
                    }
                    else h.VisW = 1f;   // [Hands] DrawnOffsetFade off: the raw physics pose, for comparing in the headset
                    Vector3 dpS = h.VisW >= 0.999f ? dp : dp * h.VisW;
                    Quaternion dqS = h.VisW >= 0.999f ? dq : Quaternion.Slerp(Quaternion.identity, dq, h.VisW);
                    vp += dpS; vq = dqS * tq;
                    SetDisp(h.Left, dpS, dqS);
                    h.VisP = dpS; h.VisQ = dqS;
                    float shownMm = dpS.magnitude * 1000f, shownDeg = Quaternion.Angle(Quaternion.identity, dqS);
                    h.OffN++; h.RawMmSum += rawMm; h.RawDegSum += rawDeg; h.ShownMmSum += shownMm; h.ShownDegSum += shownDeg;
                    if (!touchingHand)
                    {
                        h.FreeN++;
                        if (rawMm > h.FreeMaxMm) h.FreeMaxMm = rawMm;
                        if (rawDeg > h.FreeMaxDeg) h.FreeMaxDeg = rawDeg;
                    }
                    if (h.VisHave)
                    {
                        float d = (dpS - h.VisPrev).magnitude * 1000f, da = Quaternion.Angle(h.VisPrevQ, dqS);
                        h.JitSum += d; h.JitQSum += da; h.JitN++;
                        if (d > h.JitMax) h.JitMax = d;
                        if (da > h.JitQMax) h.JitQMax = da;
                        float dr = (dp - h.RawPrev).magnitude * 1000f, dra = Quaternion.Angle(h.RawPrevQ, dq);
                        h.JitRawSum += dr; h.JitRawQSum += dra;
                        if (dr > h.JitRawMax) h.JitRawMax = dr;
                        if (dra > h.JitRawQMax) h.JitRawQMax = dra;
                    }
                    h.VisPrev = dpS; h.VisPrevQ = dqS; h.RawPrev = dp; h.RawPrevQ = dq; h.VisHave = true;
                }
                else h.VisHave = false;
            }
            else h.VisHave = false;
            h.VRoot.SetPositionAndRotation(vp, vq);

            // the fingers: command (rest curl / trigger / grip) -> conform to her skin -> ease -> FCurl, which FingerStep turns into joint
            // targets (rig) or PoseFinger into bone rotations (kinematic); the visual fingers follow the simulated bones
            UpdateFingers(h, s, dt);
            if (h.RigOn) PoseFromRig(h);
            else
                for (int f = 0; f < 5; f++)
                    if (Mathf.Abs(h.FCurl[f] - h.FApplied[f]) > 0.002f) { h.FApplied[f] = h.FCurl[f]; PoseFinger(h, f); }
            PublishFingers(h);

            SetVisible(h, !s.HideVisual);

            // materials of a hand skin the game unloaded: swap in ours, keep the mesh
            if (now >= h.NextMat)
            {
                h.NextMat = now + 2f;
                for (int i = 0; i < h.Rends.Count; i++)
                {
                    var smr = h.Rends[i] as SkinnedMeshRenderer;
                    if (smr == null) continue;
                    var mats = smr.sharedMaterials;
                    bool fix = false;
                    for (int k = 0; k < mats.Length; k++) if (mats[k] == null) { mats[k] = SkinMat(); fix = true; }
                    if (fix) { smr.sharedMaterials = mats; LogOnce("mat" + h.Left, "a hand material was destroyed, using the fallback skin for it"); }
                }
            }
        }

        // The headset's tracking update runs after the apply step (right before render, as Embody found), so the controller transform used
        // above is up to a frame old by the time the frame is drawn: a hand model parented to the controller got the late pose, our
        // separate visual did not and trailed the real hand by a varying amount, which reads as a floating, unstable hand. Put the same
        // physics offset on the controller's pose as it is now, and pose the simulated fingers against the moved root again.
        static bool _brSub;

        static void BeforeRender() { PreRenderRefresh(); }

        // The refresh itself: writes an absolute pose (controller + the offset of this frame), so calling it twice in a frame is harmless.
        // Embody calls it again after correcting the view / arms in its own onBeforeRender (the controllers move with the rig it shifts).
        internal static void PreRenderRefresh()
        {
            if (_s == null) return;
            try { RefreshVisual(_left); RefreshVisual(_right); }
            catch (Exception e) { LogOnce("brerr" + e.GetType().Name, "pre-render pose error: " + e.Message); }
        }

        static void RefreshVisual(Hand h)
        {
            if (h == null || h.VGo == null || h.VRoot == null || h.VisFrame != Time.frameCount) return;
            var ctl = h.Left ? VrHands.LeftController : VrHands.RightController;
            if (ctl == null) return;
            Vector3 tp; Quaternion tq;
            Target(ctl, h.Left ? _s.LeftPos : _s.RightPos, h.Left ? _s.LeftRot : _s.RightRot, h.Spec.Align, out tp, out tq);
            if (!Finite(tp) || !Finite(tq)) return;
            h.VRoot.SetPositionAndRotation(tp + h.VisP, h.VisQ * tq);
            if (h.RigOn) PoseFromRig(h);
        }

        static void ResetTracks()
        {
            if (_left != null) { _left.Trk.Reset(); _left.VisHave = false; }
            if (_right != null) { _right.Trk.Reset(); _right.VisHave = false; }
            for (int i = 0; i < HerBody.Segs.Count; i++) HerBody.Segs[i].Trk.Reset();
        }

        // Kinematic fingers (rig off, failed, or collisions off): one finger's bones at its own curl.
        static void PoseFinger(Hand h, int f)
        {
            float c = h.FCurl[f];
            for (int j = 0; j < 3; j++)
            {
                int i = f * 3 + j, bi = h.Spec.Fin[i];
                if (bi < 0) continue;
                float max = f == 0 ? ThumbMax[j] : FingerMax[j];
                var q = h.Rest[i] * Quaternion.AngleAxis(max * c, h.AxisLocal[i]);
                h.VBones[bi].localRotation = q;
                h.PBones[bi].localRotation = q;
            }
        }

        // ================= release =================

        public static void Release()
        {
            try
            {
                DestroyHand(_left); DestroyHand(_right);
                _left = _right = null;
                DestroyHer();
                RestoreHide(_hideL); RestoreHide(_hideR);
                if (_dbgOn) SetDebug(false);
                RestoreLayers();
                if (_brSub) { Application.onBeforeRender -= BeforeRender; _brSub = false; }
                if (_handMat != null) UnityEngine.Object.Destroy(_handMat);
                if (_herMat != null) UnityEngine.Object.Destroy(_herMat);
                if (_fingerMat != null) UnityEngine.Object.Destroy(_fingerMat);
                _handMat = _herMat = _fingerMat = null; _matF = -1f;
                if (_skinMat != null) UnityEngine.Object.Destroy(_skinMat);
                _skinMat = null;
                bool had = Capsules.Count > 0;
                Capsules.Clear();
                if (had) Version++;
            }
            catch (Exception e) { Plugin.Log.LogWarning("PhysicsHands: release: " + e.Message); }
            LeftWrist = RightWrist = null;
            SetDisp(true, Vector3.zero, Quaternion.identity); SetDisp(false, Vector3.zero, Quaternion.identity);
            LeftFingersValid = RightFingersValid = false;
            _handTouchT[0] = _handTouchT[1] = -100f;
            Active = false;
            _taken = false; _retryAt = 0f; _nextBuildL = _nextBuildR = 0f; _herNext = 0f; _herNote = "";
            _herSoftness = -1f; _statusDirty = false; RebuildCapsNow = false;
            _contacts.Clear();
            _enters = _tpFar = _tpJump = _tpNan = 0;
            _frameT = -1f; _frameDt = 0f; _stepsNow = _stepSum = _stepFrames = _stepMax = _rigShifts = 0;
            _herSampleFrame = -1; _hullWant = false; _cookOk = _cookFail = _cookFrames = 0; _cookMs = 0f; _cookLogged = false;
            Status = "";
        }
    }
}
