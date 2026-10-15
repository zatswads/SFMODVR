using System;
using System.Collections.Generic;
using UnityEngine;

namespace SfMorph
{
    // The jointed finger rig of the physics hands (0.7.2): per finger two rigidbodies (the proximal phalanx; the middle and distal
    // fused into one), each on a ConfigurableJoint to its parent body with a slerp drive whose maxForce is the finger's compliance.
    // The curl target goes in through the standard SetTargetRotationLocal recipe; a tip cast slows a finger that is about to touch
    // her; the visual fingers copy the simulated bones. A hand whose rig goes wrong falls back to kinematic fingers (0.7.0).
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
        // 0.7.7: while the hand touches her the fingers give: 0.4 of the free-air compliance, so they splay and bend on her instead of
        // holding their curl (a 12 N fingertip press at 7 cm is 0.84 N*m, well over either).
        const float GiveScale = 0.4f, GiveProx = ForceProx * GiveScale, GiveFused = ForceFused * GiveScale;
        const float RangeProxMin = -10f, RangeProxMax = 100f, RangeFusedMin = -5f, RangeFusedMax = 110f;   // curl angle range (degrees, + = toward the palm)
        const float SideLimit = 8f;                                                        // proximal sideways play
        const float ConformReach = 0.016f;                                                 // tip cast length beyond the tip
        const float InertiaFloor = 1e-5f;

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
                               + " deg, X limit sign " + (_limFlip ? "swapped" : "twist = -curl") + ", side play +-" + SideLimit + " deg, tip casts "
                               + (h.TipBone[1] >= 0 ? "on" : "off"));
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
            j.enablePreprocessing = false;   // projection stays on
            j.projectionMode = JointProjectionMode.PositionAndRotation;
            j.projectionDistance = 0.005f; j.projectionAngle = 5f;

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
            h.AppliedCurl = -1f;
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

        // How fast a closing finger may keep closing (0..1): a short cast from the finger's tip along its curl motion and along the
        // finger, on her layer; the nearer her skin, the slower, and at contact the target is held.
        static float ConformAdvance(Hand h, int f)
        {
            int tb = h.TipBone[f];
            if (tb < 0 || _layerHer < 0) return 1f;
            var tipT = h.PBones[tb];
            int slot = h.Spec.Fin[f * 3 + 1] == tb ? f * 3 + 1 : f * 3;
            Vector3 tip = tipT.TransformPoint(h.TipLocal[f]);
            Vector3 axis = tipT.TransformDirection(h.AxisLocal[slot]);
            Vector3 root = h.PBones[h.Spec.Fin[f * 3]].position;
            Vector3 mv = Vector3.Cross(axis, tip - root);
            float r = h.TipR[f], len = r + ConformReach;
            int mask = 1 << _layerHer;
            float gap = float.MaxValue;
            RaycastHit hit;
            if (mv.sqrMagnitude > 1e-10f && Physics.Raycast(tip, mv.normalized, out hit, len, mask, QueryTriggerInteraction.Ignore)) gap = hit.distance - r;
            Vector3 fd = tip - tipT.position;
            if (fd.sqrMagnitude > 1e-10f && Physics.Raycast(tip, fd.normalized, out hit, len, mask, QueryTriggerInteraction.Ignore)) gap = Mathf.Min(gap, hit.distance - r);
            if (gap >= ConformReach) return 1f;
            return Mathf.Clamp01(gap / ConformReach);
        }

        // Per fixed step, before the physics step: measure the joints, check the rig is sane, decide each finger's curl (the tip cast
        // may hold it), write the joint targets.
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

            // each finger's curl: opening is always allowed; closing is slowed (held at contact) by the tip cast
            bool cast = _herBuilt && h.CollideOn;
            for (int f = 0; f < 5; f++)
            {
                float want = h.Curl, cur = h.FCurl[f];
                if (want <= cur) cur = want;
                else
                {
                    float adv = cast && want - cur > 0.0005f ? ConformAdvance(h, f) : 1f;
                    cur += (want - cur) * adv;
                }
                h.FCurl[f] = cur;
            }
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
