using System;
using System.Collections.Generic;
using UnityEngine;

namespace SfMorph
{
    // The jointed finger rig of the physics hands (0.7.2): per finger two rigidbodies (the proximal phalanx; the middle and distal
    // fused into one), each on a ConfigurableJoint to its parent body with a slerp drive whose maxForce is the finger's compliance.
    // The curl target goes in through the standard SetTargetRotationLocal recipe; since 0.8.0 each finger's curl is its command (rest
    // curl, trigger, grip) stopped at her skin by a FingerFan sweep (UpdateFingers); the visual fingers copy the simulated bones. A hand
    // whose rig goes wrong falls back to kinematic fingers (0.7.0).
    internal static partial class PhysicsHands
    {
        static PhysicsHands()
        {
            for (int i = 0; i < 15; i++)
                LeftFingerLocal[i] = RightFingerLocal[i] = LeftFingerRest[i] = RightFingerRest[i] = Quaternion.identity;
        }

        // Last contact time of a hand collider with anything of hers, per hand (left, right), Time.fixedTime.
        static readonly float[] _handTouchT = { -100f, -100f };

        // True while a physics-hand collider touches her capsule of that name (set from the contact probes, so it stays true for
        // about two fixed steps after the last contact). Her capsules: pelvis, spine, chest, neck, head, thigh_L, thigh_R, calf_L,
        // calf_R, upperarm_L, upperarm_R, forearm_L, forearm_R, butt_L, butt_R (only those whose bones exist).
        public static bool TouchingHer(string herBone)
        {
            if (herBone == null || !_herBuilt) return false;
            float t = Time.fixedTime, win = 2.5f * Time.fixedDeltaTime + 0.01f;
            var g = HerBody.Find(herBone);
            return g != null && t - g.TouchT <= win;
        }

        // ---- tuning (from the design) ----
        const float MassProx = 0.04f, MassFused = 0.03f;
        const float SpringK = 100f, DamperK = 5f, ForceProx = 0.3f, ForceFused = 0.15f;   // slerp drive; maxForce (N*m) is the compliance
        // 0.7.7: while the hand touches her the fingers give: a fraction of the free-air compliance (0.4 until 0.8.0, now 0.3: softer, the
        // fan stops them at her skin so they no longer need to be stiff to hold their place), so they splay and bend on her instead of
        // holding their curl (a 12 N fingertip press at 7 cm is 0.84 N*m, well over either).
        const float GiveScale = 0.3f, GiveProx = ForceProx * GiveScale, GiveFused = ForceFused * GiveScale;
        const float RangeProxMin = -10f, RangeProxMax = 100f, RangeFusedMin = -5f, RangeFusedMax = 110f;
        const float ThumbRangeProx = 50f, ThumbRangeFused = 80f;   // curl angle range (degrees, + = toward the palm)
        const float SideLimit = 8f;                                                        // proximal sideways play
        const float InertiaFloor = 1e-5f;
        // 0.8.0 finger commands and conform (section 5): the rest curl per finger at [Hands] RestCurl 0.25 (scaled by RestCurl / 0.25), the
        // share of the touching fingers' curl the others (index and pinky half, middle and ring three quarters) follow, and the ease of
        // FCurl toward its target: exponential at EaseRate per second, never faster than EaseMax per second (0 -> 1 takes >= 0.125 s)
        static readonly float[] RestCurlF = { 0.15f, 0.20f, 0.25f, 0.30f, 0.35f };
        static readonly float[] FollowerK = { 0f, 0.5f, 0.75f, 0.75f, 0.5f };
        const float EaseRate = 15f, EaseMax = 8f;
        static readonly string[] FingerTag = { "thumb", "index", "middle", "ring", "pinky" };
        static readonly float[] _cmd = new float[5], _contact = new float[5];   // UpdateFingers scratch (the main thread only)
        static readonly int[] _fseg = new int[5];

        // The sign of the joint's angular X limit against the curl angle is the one thing this rig can't know without running:
        // the drive target is the inverse of the wanted rotation (the recipe), and the limit is assumed to share that convention
        // (twist = -curl). If a finger sits far short of its target with nothing touching it, the signs are swapped once, for
        // every finger, and logged. Each direction is tried once (so a wrong flip can be undone once, after 0.5 s of still being stuck).
        static bool _limFlip;
        static readonly bool[] _limTriedDir = new bool[2];   // [0]: flipped away from the default; [1]: flipped back

        sealed class Fing
        {
            public int Finger, Joint, Slot, Bone;
            public Rigidbody Rb, Conn;
            public ConfigurableJoint J;
            public PhysicsHandProbe Probe;
            public Vector3 AxisL;
            public Quaternion W2J, Rest;            // joint space (recipe) and the bone's rest local rotation
            public Quaternion RestRel;               // rest rotation relative to the connected body
            public Vector3 RestPosW;                 // rest offset from the connected body, in its frame
            public float MaxDeg, AMin, AMax;         // curl at full grip; allowed range
            public float CmdFrac = -1f, Cmd, Meas, Swing;
            public float Force;                      // the slerp drive's maximumForce as last written (changed only when it differs)
            public Quaternion Prev = Quaternion.identity, Cur = Quaternion.identity;   // pose relative to the palm, last two fixed steps
        }

        static float JointMax(int finger, int joint) { return finger == 0 ? ThumbMax[joint] : FingerMax[joint]; }

        // Builds the rig on a hand whose skeleton and colliders exist: rest pose, then bodies and joints.
        static void EnableRig(Hand h)
        {
            if (h.RigOn || h.RigFailed || h.PGo == null || h.Rb == null) return;
            var sp = h.Spec;
            for (int i = 0; i < 15; i++)   // the joints' zero is the open rest hand
            {
                int bi = sp.Fin[i];
                if (bi < 0) continue;
                h.PBones[bi].localPosition = sp.Bones[bi].Pos;
                h.PBones[bi].localRotation = sp.Bones[bi].Rot;
            }
            Physics.SyncTransforms();
            h.Rig.Clear();
            for (int i = 0; i < 15; i++) h.FingAt[i] = null;
            float mass = PalmMass;
            for (int f = 0; f < 5; f++)
            {
                int b0 = sp.Fin[f * 3], b1 = sp.Fin[f * 3 + 1];
                if (b0 < 0) continue;
                var p = MakeFing(h, f, 0, null);
                if (p == null) continue;
                mass += MassProx;
                if (b1 >= 0 && MakeFing(h, f, 1, p) != null) mass += MassFused;
            }
            if (h.Rig.Count == 0) { h.TotalMass = PalmMass; return; }
            h.TotalMass = mass;
            h.RigOn = true;
            h.CurValid = false; h.BadT = 0f; h.StuckT = 0f;
            for (int f = 0; f < 5; f++) h.FWritten[f] = -1f;
            Plugin.Log.LogInfo("PhysicsHands: " + (h.Left ? "left" : "right") + " finger rig: " + (h.Rig.Count + 1) + " rigidbodies (palm " + PalmMass.ToString("0.00") + " kg, fingers "
                               + MassProx.ToString("0.00") + "/" + MassFused.ToString("0.00") + " kg, hand " + mass.ToString("0.00") + " kg), " + h.Rig.Count + " configurable joints, slerp drive spring "
                               + SpringK + " damper " + DamperK + " maxForce " + ForceProx + "/" + ForceFused + " N*m, curl range " + RangeProxMin + ".." + RangeProxMax + " / " + RangeFusedMin + ".." + RangeFusedMax
                               + " deg, X limit sign " + (_limFlip ? "swapped" : "twist = -curl") + ", side play +-" + SideLimit + " deg, conform fans "
                               + (h.Fan[1] != null ? "on" : "off"));
            _statusDirty = true;
        }

        static Fing MakeFing(Hand h, int f, int joint, Fing parent)
        {
            var sp = h.Spec;
            int slot = f * 3 + joint, bi = sp.Fin[slot];
            if (bi < 0) return null;
            var tr = h.PBones[bi];
            var conn = parent != null ? parent.Rb : h.Rb;
            bool prox = joint == 0;

            var rb = tr.gameObject.AddComponent<Rigidbody>();
            rb.mass = prox ? MassProx : MassFused;
            rb.useGravity = false; rb.drag = 0f; rb.angularDrag = 0f;
            rb.interpolation = RigidbodyInterpolation.None;   // the visual interpolates the relative pose itself (PoseFromRig)
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.maxAngularVelocity = 30f; rb.maxDepenetrationVelocity = 2f;
            rb.solverIterations = 16; rb.solverVelocityIterations = 4; rb.sleepThreshold = 0f;
            var it = rb.inertiaTensor;
            rb.inertiaTensor = new Vector3(Mathf.Max(it.x, InertiaFloor), Mathf.Max(it.y, InertiaFloor), Mathf.Max(it.z, InertiaFloor));

            var g = new Fing { Finger = f, Joint = joint, Slot = slot, Bone = bi, Rb = rb, Conn = conn, AxisL = h.AxisLocal[slot].normalized, Rest = h.Rest[slot], MaxDeg = JointMax(f, joint) };
            g.AMin = prox ? RangeProxMin : RangeFusedMin;
            g.AMax = prox ? RangeProxMax : RangeFusedMax;
            // 0.11.7 (user: the thumb closes wrong and bends unrealistically): a thumb joint bends far less than a finger's (about 50 deg
            // at the base, 80 at the fused middle + tip) and barely backwards
            if (f == 0) { g.AMin = prox ? -5f : -5f; g.AMax = prox ? ThumbRangeProx : ThumbRangeFused; }
            Vector3 sec = h.DirLocal[slot];
            sec -= g.AxisL * Vector3.Dot(sec, g.AxisL);
            if (sec.sqrMagnitude < 1e-6f) sec = Vector3.Cross(g.AxisL, Mathf.Abs(g.AxisL.y) < 0.9f ? Vector3.up : Vector3.right);
            sec.Normalize();
            Vector3 right = g.AxisL, fwd = Vector3.Cross(right, sec).normalized, up = Vector3.Cross(fwd, right).normalized;
            g.W2J = Quaternion.LookRotation(fwd, up);

            var ct = conn.transform;
            g.RestRel = Quaternion.Inverse(ct.rotation) * tr.rotation;
            g.RestPosW = Quaternion.Inverse(ct.rotation) * (tr.position - ct.position);

            var j = tr.gameObject.AddComponent<ConfigurableJoint>();
            j.autoConfigureConnectedAnchor = true;
            j.anchor = Vector3.zero;                  // the bone's origin is the joint
            j.axis = g.AxisL; j.secondaryAxis = sec;
            j.connectedBody = conn;
            j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Locked;
            j.angularXMotion = ConfigurableJointMotion.Limited;
            j.angularYMotion = j.angularZMotion = prox ? ConfigurableJointMotion.Limited : ConfigurableJointMotion.Locked;
            if (prox)
            {
                var side = new SoftJointLimit(); side.limit = SideLimit;
                j.angularYLimit = side; j.angularZLimit = side;
            }
            g.J = j;
            ApplyLimits(g);
            j.rotationDriveMode = RotationDriveMode.Slerp;
            var drive = new JointDrive();
            drive.positionSpring = SpringK; drive.positionDamper = DamperK; drive.maximumForce = prox ? ForceProx : ForceFused;
            g.Force = drive.maximumForce;
            j.slerpDrive = drive;
            j.targetRotation = Quaternion.identity;
            j.enableCollision = false;
            j.enablePreprocessing = false;
            // 0.11.20 (user: the fingers still pass straight through her): projection snapped a finger back onto its anchor whenever the joint
            // stretched > 5 mm / 5 deg, and PhysX projection ignores contacts. A finger pressed against her (tip first, or flat on a curving
            // surface where it cannot bend back past its limit) was pushed toward the palm by the contact, stretched the joint, and was
            // teleported forward into her every step while the palm (60 N follow) kept coming. Without projection the contact wins and the
            // stretch goes through the joint into the palm, which stops. The joint sees the palm (and a fused bone its proximal) as at most
            // 3x the finger's mass (connectedMassScale, an inverse-mass scale): the 12:1 palm/finger ratio is what let the joint stretch.
            j.projectionMode = JointProjectionMode.None;
            float connMass = conn.mass, ownMass = rb.mass;
            j.massScale = 1f;
            j.connectedMassScale = connMass > 3f * ownMass ? connMass / (3f * ownMass) : 1f;

            g.Probe = tr.gameObject.AddComponent<PhysicsHandProbe>();
            g.Probe.Left = h.Left;
            h.Rig.Add(g);
            h.FingAt[slot] = g;
            return g;
        }

        static void ApplyLimits(Fing g)
        {
            var lo = new SoftJointLimit(); var hi = new SoftJointLimit();
            // curl a (+ toward the palm) drives the joint to twist -a (see _limFlip)
            lo.limit = _limFlip ? g.AMin : -g.AMax;
            hi.limit = _limFlip ? g.AMax : -g.AMin;
            g.J.lowAngularXLimit = lo; g.J.highAngularXLimit = hi;
        }

        // Drops the rig (kinematic fingers, posed by the curl like 0.7.0): joints, bodies and probes go, the colliders fall back
        // to the palm's compound, the bones return to the rest pose.
        static void DisableRig(Hand h)
        {
            if (h.Rig.Count == 0 && !h.RigOn) return;
            try
            {
                foreach (var g in h.Rig) if (g.J != null) UnityEngine.Object.DestroyImmediate(g.J);
                foreach (var g in h.Rig)
                {
                    if (g.Probe != null) UnityEngine.Object.DestroyImmediate(g.Probe);
                    if (g.Rb != null) UnityEngine.Object.DestroyImmediate(g.Rb);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("PhysicsHands: dropping the finger rig: " + e.Message); }
            h.Rig.Clear();
            for (int i = 0; i < 15; i++) h.FingAt[i] = null;
            h.RigOn = false;
            h.TotalMass = PalmMass;
            for (int f = 0; f < 5; f++) h.FApplied[f] = -1f;   // the kinematic fingers are posed again
            h.CurValid = false;
            var sp = h.Spec;
            if (h.PBones != null)
                for (int i = 0; i < 15; i++)
                {
                    int bi = sp.Fin[i];
                    if (bi < 0 || h.PBones[bi] == null) continue;
                    h.PBones[bi].localPosition = sp.Bones[bi].Pos;
                    h.PBones[bi].localRotation = sp.Bones[bi].Rot;
                }
            _statusDirty = true;
        }

        static void FailRig(Hand h, string why)
        {
            string side = h.Left ? "left" : "right";
            Plugin.Log.LogWarning("PhysicsHands: " + side + " finger rig failed (" + why + "): kinematic fingers for this hand from now on");
            h.RigFailed = true;
            DisableRig(h);
        }

        // The wanted joint target for a curl angle: the SetTargetRotationLocal recipe, written inline:
        // joint space w2j = LookRotation(cross(axis, secondary), cross(forward, axis)); target = Inverse(w2j) * Inverse(desired) * start * w2j
        // with start the bone's rest local rotation and desired = start rotated about its own curl axis.
        static Quaternion DriveTarget(Fing g, float degrees)
        {
            Quaternion desired = g.Rest * Quaternion.AngleAxis(degrees, g.AxisL);
            return Quaternion.Inverse(g.W2J) * Quaternion.Inverse(desired) * g.Rest * g.W2J;
        }

        // Twist of a rotation about a unit axis, in degrees (-180..180), and the rest of it (the swing) in degrees.
        static float Twist(Quaternion d, Vector3 axis, out float swingDeg)
        {
            if (d.w < 0f) d = new Quaternion(-d.x, -d.y, -d.z, -d.w);
            float p = d.x * axis.x + d.y * axis.y + d.z * axis.z;
            var tw = new Quaternion(axis.x * p, axis.y * p, axis.z * p, d.w);
            float m = Mathf.Sqrt(tw.x * tw.x + tw.y * tw.y + tw.z * tw.z + tw.w * tw.w);
            if (!(m > 1e-6f)) { swingDeg = 180f; return 0f; }
            tw = new Quaternion(tw.x / m, tw.y / m, tw.z / m, tw.w / m);
            swingDeg = Quaternion.Angle(Quaternion.identity, d * Quaternion.Inverse(tw));
            return 2f * Mathf.Atan2(p, d.w) * Mathf.Rad2Deg;
        }

        // ---- finger conform (0.8.0, design section 5) ----
        // Replaces the closed-loop tip casts of 0.7.x (ConformAdvance, once per fixed step, which also had to guess the motion direction):
        // every finger has a FingerFan, the places its tip and last capsule pass through at 8 curl fractions, in the palm frame, built
        // once from the rest pose. Each rendered frame the fan is laid on the palm's RIGIDBODY pose (where the colliders really are, not the
        // drawn hand) and the first fraction that touches her is the stop curl. It doesn't depend on where the finger is now, so closing
        // can't change what it senses, and there is no per-step cast.

        // One fan per finger, from the same bones, rest rotations, curl axes and ranges the rig and PoseFinger use. Joint 0 is the
        // proximal bone, joint 1 the fused middle + distal (the collider is rigid from there to the tip); a finger with one bone has one
        // joint. The tip is the end B of the finger's last collider.
        static void BuildFans(Hand h)
        {
            var sp = h.Spec;
            float u = sp.Scale;
            int built = 0;
            for (int f = 0; f < 5; f++)
            {
                h.Fan[f] = null;
                int b0 = sp.Fin[f * 3], b1 = sp.Fin[f * 3 + 1];
                if (b0 < 0) continue;
                int joints = b1 >= 0 ? 2 : 1;
                string tag = FingerTag[f] + (b1 >= 0 ? "B" : "");
                ShapeSpec tipShape = null;
                foreach (var sh in sp.Cols) if (!sh.Box && sh.Tag == tag) { tipShape = sh; break; }
                if (tipShape == null) continue;
                var P = new Vector3[joints + 1]; var Q = new Quaternion[joints]; var axis = new Vector3[joints]; var max = new float[joints];
                for (int j = 0; j < joints; j++)
                {
                    Vector3 pos, sc; Quaternion rot;
                    Decompose(sp.Bones[sp.Fin[f * 3 + j]].M, out pos, out rot, out sc);
                    P[j] = pos * u; Q[j] = rot; axis[j] = h.AxisLocal[f * 3 + j]; max[j] = JointMax(f, j);
                }
                P[joints] = tipShape.B * u;
                var fan = new FingerFan();
                fan.Build(joints, P, Q, axis, max, f == 0 ? ThumbR : FingerR);
                if (fan.Built) { h.Fan[f] = fan; built++; }
            }
            LogOnce("fan", "finger conform: fan " + FingerFan.Steps + " steps x " + FingerFan.ProbesPerStep + " probes per finger, radius " + (FingerR * 1000f).ToString("0") + " mm (thumb " + (ThumbR * 1000f).ToString("0.0")
                           + "), enter +" + (FingerFan.EnterPad * 1000f).ToString("0") + " mm / stay -" + (FingerFan.ExitPad * 1000f).ToString("0") + " mm; followers " + FollowerK[1].ToString("0.##") + "/" + FollowerK[2].ToString("0.##")
                           + "; ease " + EaseRate.ToString("0") + "/s; finger friction " + FingerFriction.ToString("0.00") + " (Minimum); drive force on touch " + GiveScale.ToString("0.0#") + "x");
            if (built == 0) LogOnce("fannone" + h.Left, (h.Left ? "left" : "right") + " hand: no finger fans (no finger colliders to sweep)");
        }

        // Per rendered frame: each finger's curl command from the controller (section 5.1), its contact curl from the fan (5.3), the
        // target after followers and the give (5.4), eased into FCurl. FingerStep only turns FCurl into joint targets.
        static void UpdateFingers(Hand h, HandsSettings s, float dt)
        {
            // 0.8.0 (user): one grab button closes the whole hand from its rest curl (Grip.GrabButton, the trigger by default: the grip is
            // the game's menu button); no index-only pointing pose
            float grab = Mathf.Clamp01(h.Left ? Grip.GrabLeft : Grip.GrabRight);
            float rs = Mathf.Clamp01(s.RestCurl) / 0.25f;
            for (int f = 0; f < 5; f++) _cmd[f] = Mathf.Lerp(Mathf.Clamp01(RestCurlF[f] * rs), 1f, grab);

            // contact: the fan on the rigidbody's pose, once per frame per finger (<= 16 CheckSphere + one cast each)
            bool cast = s.FingerConform && _herBuilt && _layerHer >= 0 && h.CollideOn && !h.Kin && h.Rb != null;
            Vector3 pos = Vector3.zero; Quaternion rot = Quaternion.identity;
            if (cast)
            {
                pos = h.Rb.position; rot = h.Rb.rotation;
                if (!Finite(pos) || !Finite(rot)) cast = false;
            }
            int herMask = 1 << _layerHer;
            float avg = 0f; int nTouch = 0;
            for (int f = 0; f < 5; f++)
            {
                float c = 1f; int seg = -1, step = -1;
                var fan = h.Fan[f];
                if (cast && fan != null) c = fan.ConformPhysics(pos, rot, 1f, herMask, h.FanStep[f], _segOfFn, out seg, out step);
                if (!(c >= 0f)) c = 1f;   // NaN
                // probe flips: the first touching step jumping by two or more between consecutive frames is chatter
                if (step >= 0 && h.FanStep[f] >= 0 && Mathf.Abs(step - h.FanStep[f]) >= 2) h.FFlipN++;
                h.FanStep[f] = step;
                _contact[f] = c; _fseg[f] = seg;
                if (c < 1f && f != 0) { avg += c; nTouch++; }
            }
            if (nTouch > 0) avg /= nTouch;

            float soft = 0.5f + Mathf.Clamp01(s.Softness);
            float edt = Mathf.Min(dt, 0.1f);
            for (int f = 0; f < 5; f++)
            {
                float target = _cmd[f];
                if (nTouch > 0 && _contact[f] >= 1f && f != 0) target = Mathf.Max(target, FollowerK[f] * avg);   // DL's followers wrap along
                if (_contact[f] < 1f)
                {
                    float give = (_fseg[f] >= 0 ? HerBody.ClaspGive(_fseg[f]) : 0.06f) * soft;
                    target = Mathf.Min(target, _contact[f] + give);
                    h.FHeldN[f]++; h.FContactN++; h.FContactSum += _contact[f]; h.FGiveN++; h.FGiveSum += give;
                }
                target = Mathf.Clamp01(target);
                // the ease (DL: 0.15 s): exponential at EaseRate (time constant 67 ms), no faster than EaseMax per second
                float cur = h.FCurl[f];
                float stepC = (target - cur) * (1f - Mathf.Exp(-EaseRate * edt));
                float lim = EaseMax * edt;
                h.FCurl[f] = Mathf.Clamp01(cur + Mathf.Clamp(stepC, -lim, lim));
            }
        }

        // Per fixed step, before the physics step: measure the joints, check the rig is sane, write the joint targets from FCurl
        // (UpdateFingers decides it, once per rendered frame).
        static void FingerStep(Hand h, float dt)
        {
            var sp = h.Spec;
            float now = Time.fixedTime;
            bool touching = now - _handTouchT[h.Left ? 0 : 1] < 0.3f;
            float worstProx = 0f, worstMeas = 0f, worstCmd = 0f; bool badNow = false; string why = null;
            bool stuckAtWall = true;   // every proximal finger that is far from its target sits at the limit wall
            for (int i = 0; i < h.Rig.Count; i++)
            {
                var g = h.Rig[i];
                if (g.Rb == null || g.Conn == null || g.J == null) { FailRig(h, "a finger body was destroyed"); return; }
                Vector3 pos = g.Rb.position; Quaternion rot = g.Rb.rotation;
                if (!Finite(pos) || !Finite(rot)) { FailRig(h, "NaN in a finger body"); return; }
                Quaternion cr = g.Conn.rotation;
                float sw;
                g.Meas = Twist(Quaternion.Inverse(g.RestRel) * (Quaternion.Inverse(cr) * rot), g.AxisL, out sw);
                g.Swing = sw;
                if (sw > 60f) { badNow = true; why = "finger swung " + sw.ToString("0") + " deg off its curl axis"; }
                Vector3 expect = g.Conn.position + cr * g.RestPosW;
                if ((pos - expect).sqrMagnitude > 0.04f * 0.04f) { badNow = true; why = "finger joint drifted " + (pos - expect).magnitude.ToString("0.000") + " m"; }
                if (g.Joint == 0 && g.Finger > 0)
                {
                    float e = Mathf.Abs(g.Cmd - g.Meas);
                    if (g.Cmd >= 30f && e > worstProx) { worstProx = e; worstMeas = g.Meas; worstCmd = g.Cmd; }
                    if (g.Cmd >= 30f && e > 25f && Mathf.Abs(g.Meas) > 12f) stuckAtWall = false;
                }
            }

            CaptureRig(h);   // the visual fingers interpolate between the last two of these (one per fixed step)

            for (int i = 0; i < h.Rig.Count; i++)
            {
                var g = h.Rig[i];
                // the give: lower drive force while touching her; the JointDrive struct is reassigned only when the wanted force changes
                float wantF = g.Joint == 0 ? (touching ? GiveProx : ForceProx) : (touching ? GiveFused : ForceFused);
                if (Mathf.Abs(wantF - g.Force) > 1e-5f)
                {
                    var drv = g.J.slerpDrive;
                    drv.maximumForce = wantF;
                    g.J.slerpDrive = drv;
                    g.Force = wantF;
                    LogOnce("fingergive", "fingers give on contact: slerp maxForce " + GiveProx.ToString("0.###") + "/" + GiveFused.ToString("0.###") + " N*m while a hand touches her (" + ForceProx.ToString("0.###") + "/" + ForceFused.ToString("0.###") + " otherwise)");
                }
                float c = h.FCurl[g.Finger];
                if (Mathf.Abs(c - g.CmdFrac) < 5e-4f) continue;
                g.CmdFrac = c;
                g.Cmd = g.MaxDeg * c;
                g.J.targetRotation = DriveTarget(g, g.Cmd);
            }
            // the kinematic distal bones of the physics skeleton follow the fused bone's measured curl
            for (int f = 0; f < 5; f++)
            {
                int bd = sp.Fin[f * 3 + 2];
                var mid = h.FingAt[f * 3 + 1];
                if (bd < 0 || mid == null) continue;
                float frac = Mathf.Clamp01(mid.Meas / Mathf.Max(1f, mid.MaxDeg));
                h.PBones[bd].localRotation = h.Rest[f * 3 + 2] * Quaternion.AngleAxis(JointMax(f, 2) * frac, h.AxisLocal[f * 3 + 2]);
            }

            // a finger that stays far from its target with nothing touching the hand: the limit sign may be the wrong way round (once),
            // else the rig is broken
            if (!touching && worstProx > 25f) h.StuckT += dt; else h.StuckT = 0f;
            bool flippedBefore = _limTriedDir[0] || _limTriedDir[1];
            int dir = _limFlip ? 1 : 0;
            if (h.StuckT > (flippedBefore ? 0.5f : 0.35f) && stuckAtWall && !_limTriedDir[dir])
            {
                _limTriedDir[dir] = true; _limFlip = !_limFlip;
                foreach (var hh in new[] { _left, _right })
                    if (hh != null) foreach (var g in hh.Rig) if (g.J != null) ApplyLimits(g);
                Plugin.Log.LogWarning("PhysicsHands: " + (h.Left ? "left" : "right") + " fingers stayed " + worstProx.ToString("0") + " deg short of their curl target (cmd " + worstCmd.ToString("0") + ", measured " + worstMeas.ToString("0")
                                      + ") with nothing touching: joint X limit sign swapped (" + (_limFlip ? "limit = +curl" : "limit = -curl") + ")");
                h.StuckT = 0f;
            }
            if (!touching && worstProx > 60f) { badNow = true; why = "finger " + worstProx.ToString("0") + " deg off its target"; }
            if (badNow) { h.BadT += dt; if (h.BadT > 1f) FailRig(h, why); }
            else h.BadT = 0f;
        }

        // The fingers' pose relative to the palm, once per fixed step (FingerStep): Prev is the step before.
        static void CaptureRig(Hand h)
        {
            if (h.Rb == null) return;
            Quaternion ip = Quaternion.Inverse(h.Rb.rotation);
            for (int i = 0; i < h.Rig.Count; i++)
            {
                var g = h.Rig[i];
                if (g.Rb == null) continue;
                Quaternion rel = ip * g.Rb.rotation;
                g.Prev = h.CurValid ? g.Cur : rel;
                g.Cur = rel;
            }
            h.CurValid = true;
        }

        // The visual fingers copy the simulated bones: the proximal and the fused bone's pose relative to the palm (interpolated between
        // the last two fixed steps, as a rigidbody with Interpolate would) composed onto the visual root; the distal bone follows
        // the fused bone's curl.
        static void PoseFromRig(Hand h)
        {
            if (h.Rb == null || h.VBones == null) return;
            if (!h.CurValid) CaptureRig(h);   // no fixed step has run since the rig was built / the hand teleported
            float dt = Time.fixedDeltaTime;
            float a = dt > 0f ? Mathf.Clamp01((Time.time - Time.fixedTime) / dt) : 1f;
            Quaternion vr = h.VRoot.rotation;
            var rels = h.RelScratch;
            for (int i = 0; i < 15; i++) rels[i] = Quaternion.identity;
            for (int i = 0; i < h.Rig.Count; i++)
            {
                var g = h.Rig[i];
                Quaternion rel = Quaternion.Slerp(g.Prev, g.Cur, a);
                rels[g.Slot] = rel;
                h.VBones[g.Bone].rotation = vr * rel;
            }
            for (int f = 0; f < 5; f++)
            {
                int bd = h.Spec.Fin[f * 3 + 2];
                var mid = h.FingAt[f * 3 + 1]; var prox = h.FingAt[f * 3];
                if (bd < 0) continue;
                float frac;
                if (mid != null && prox != null)
                {
                    float sw;
                    Quaternion relMid = Quaternion.Inverse(rels[f * 3]) * rels[f * 3 + 1];
                    float tw = Twist(Quaternion.Inverse(mid.RestRel) * relMid, mid.AxisL, out sw);
                    frac = Mathf.Clamp01(tw / Mathf.Max(1f, mid.MaxDeg));
                }
                else frac = h.FCurl[f];
                h.VBones[bd].localRotation = h.Rest[f * 3 + 2] * Quaternion.AngleAxis(JointMax(f, 2) * frac, h.AxisLocal[f * 3 + 2]);
            }
        }

        // The fingers' local rotations (and their rest) for Embody.
        static void PublishFingers(Hand h)
        {
            var loc = h.Left ? LeftFingerLocal : RightFingerLocal;
            var rest = h.Left ? LeftFingerRest : RightFingerRest;
            var curl = h.Left ? LeftFingerCurlDeg : RightFingerCurlDeg;
            for (int i = 0; i < 15; i++)
            {
                int bi = h.Spec.Fin[i];
                if (bi >= 0)
                {
                    loc[i] = h.VBones[bi].localRotation; rest[i] = h.Rest[i];
                    float sw;
                    float c = Twist(Quaternion.Inverse(h.Rest[i]) * loc[i], h.AxisLocal[i].normalized, out sw);
                    curl[i] = float.IsNaN(c) || float.IsInfinity(c) ? 0f : c;
                }
                else { loc[i] = Quaternion.identity; rest[i] = Quaternion.identity; curl[i] = 0f; }
            }
            if (h.Left) LeftFingersValid = true; else RightFingersValid = true;
        }
    }
}
