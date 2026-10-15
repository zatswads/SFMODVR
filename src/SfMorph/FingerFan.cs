using System;
using UnityEngine;

namespace SfMorph
{
    // 0.8.0 (design-0.8.0 section 5.2): a finger's curl sweep, fixed to the palm: the tip and the last capsule's midpoint at Steps curl
    // fractions, from forward kinematics of the rest pose, built once. "First fraction that touches" is the stop curl: open-loop (it
    // depends only on the palm pose, never on where the finger is now), so closing the finger can't change what it senses. DynamicLimbs'
    // probes lie along the OPEN finger because it can't pose bones; we own the bones, so the probes are the places the finger really
    // passes through while it closes.
    internal sealed class FingerFan
    {
        public const int Steps = 8;                                          // curl fractions 0, 1/7, ..., 1
        public const int ProbesPerStep = 2;                                  // [0] the tip, [1] the midpoint of the last capsule
        public const int MaxJoints = 3;
        public const float EnterPad = 0.003f, ExitPad = 0.001f;              // hysteresis of the physics variant (m): enter R + 3 mm, stay R + 4 mm
        public readonly Vector3[] Probe = new Vector3[Steps * ProbesPerStep];   // in the PALM (wrist) frame, metres
        // the joint origins per step (JointP[step * MaxJoints + j], palm frame), for Embody's ghost capsules; Joints = how many are valid
        public readonly Vector3[] JointP = new Vector3[Steps * MaxJoints];
        public int Joints;
        public float R;                                                      // probe radius (metres)
        public bool Built;

        static readonly Collider[] _buf = new Collider[4];

        // joints 1..3 (physics hand: proximal + fused = 2, his hand 3). P: joint origins [0..joints-1] + the tip [joints], palm frame (m).
        // Q: rest rotations of the joints in the palm frame. axisL: curl axis in each joint's own frame. maxDeg: curl at c = 1.
        public void Build(int joints, Vector3[] P, Quaternion[] Q, Vector3[] axisL, float[] maxDeg, float r)
        {
            R = r; Built = false; Joints = 0;
            if (joints < 1 || joints > MaxJoints || P == null || Q == null || axisL == null || maxDeg == null) return;
            if (P.Length < joints + 1 || Q.Length < joints || axisL.Length < joints || maxDeg.Length < joints) return;
            // the rest chain as relative transforms: rel[j] = rotation relative to the previous joint, dOff[j] = origin offset in the previous frame
            var rel = new Quaternion[joints]; var dOff = new Vector3[joints]; var q = new Quaternion[joints]; var p = new Vector3[joints];
            for (int j = 0; j < joints; j++)
            {
                rel[j] = j == 0 ? Q[0] : Quaternion.Inverse(Q[j - 1]) * Q[j];
                dOff[j] = j == 0 ? Vector3.zero : Quaternion.Inverse(Q[j - 1]) * (P[j] - P[j - 1]);
            }
            Vector3 dTip = Quaternion.Inverse(Q[joints - 1]) * (P[joints] - P[joints - 1]);
            for (int k = 0; k < Steps; k++)
            {
                float c = CurlAt(k);
                for (int j = 0; j < joints; j++)
                {
                    if (j == 0) { q[0] = rel[0] * Quaternion.AngleAxis(maxDeg[0] * c, axisL[0]); p[0] = P[0]; }
                    else { q[j] = q[j - 1] * rel[j] * Quaternion.AngleAxis(maxDeg[j] * c, axisL[j]); p[j] = p[j - 1] + q[j - 1] * dOff[j]; }
                    JointP[k * MaxJoints + j] = p[j];
                }
                Vector3 tip = p[joints - 1] + q[joints - 1] * dTip;
                Probe[k * ProbesPerStep] = tip;
                Probe[k * ProbesPerStep + 1] = (p[joints - 1] + tip) * 0.5f;
            }
            Joints = joints;
            Built = true;
        }

        public static float CurlAt(int step) { return (float)step / (Steps - 1); }

        // analytic variant (Embody): the fan placed at (pos, rot, scale), tested against HerBody's skin shapes. The contact curl in [0, 1]
        // (1 = nothing touched up to full curl) and the segment hit (-1 none). Stateless: the first step that touches, refined by four
        // bisections along the straight line between the two steps' probe positions (the arc between 1/7 curl steps is short).
        public float ConformAnalytic(Vector3 pos, Quaternion rot, float scale, out int segIndex)
        {
            segIndex = -1;
            if (!Built || !HerBody.Built) return 1f;
            float r = R * scale;
            for (int k = 0; k < Steps; k++)
                for (int q = 0; q < ProbesPerStep; q++)
                {
                    int sg; float d; Vector3 n;
                    Vector3 p = pos + rot * (Probe[k * ProbesPerStep + q] * scale);
                    if (!HerBody.SkinDepth(p, r, out sg, out d, out n) || !(d > 0f)) continue;
                    segIndex = sg;
                    if (k == 0) return 0f;
                    Vector3 a = Probe[(k - 1) * ProbesPerStep + q], b = Probe[k * ProbesPerStep + q];
                    float lo = 0f, hi = 1f;
                    for (int i = 0; i < 4; i++)
                    {
                        float t = 0.5f * (lo + hi);
                        int sg2; float d2; Vector3 n2;
                        Vector3 pm = pos + rot * (Vector3.Lerp(a, b, t) * scale);
                        if (HerBody.SkinDepth(pm, r, out sg2, out d2, out n2) && d2 > 0f) { hi = t; segIndex = sg2; } else lo = t;
                    }
                    return CurlAt(k - 1) + 0.5f * (lo + hi) * (CurlAt(k) - CurlAt(k - 1));
                }
            return 1f;
        }

        // physics variant (physics hands): CheckSphere on `herMask`, enter radius R + EnterPad, stay radius R + EnterPad + ExitPad (larger: real hysteresis) on the probes of the
        // step that touched last time (`prevStep`, -1 none): a fingertip hovering at the edge of her skin doesn't flip between touching and
        // not every frame. `segOf` maps the collider hit to a segment. Returns the contact curl in [0, 1] (1 = nothing within full curl);
        // `step` is the first step that touched (-1 none). Allocation-free: CheckSphere / SphereCast / OverlapSphereNonAlloc.
        public float ConformPhysics(Vector3 pos, Quaternion rot, float scale, int herMask, int prevStep, Func<Collider, int> segOf,
                                    out int segIndex, out int step)
        {
            segIndex = -1; step = -1;
            if (!Built || herMask == 0) return 1f;
            float r = R * scale;
            int hk = -1; Vector3 hp = Vector3.zero;
            for (int k = 0; k < Steps && hk < 0; k++)
            {
                float rs = k == prevStep ? r + EnterPad + ExitPad : r + EnterPad;   // stay radius LARGER than enter: leave at 4 mm, enter at 3
                for (int q = 0; q < ProbesPerStep; q++)
                {
                    Vector3 p = pos + rot * (Probe[k * ProbesPerStep + q] * scale);
                    if (Physics.CheckSphere(p, rs, herMask, QueryTriggerInteraction.Ignore)) { hk = k; hp = p; break; }
                }
            }
            if (hk < 0) return 1f;
            step = hk;
            if (hk == 0)
            {
                segIndex = SegAt(hp, r + EnterPad, herMask, segOf);
                return 0f;
            }
            Vector3 ta = pos + rot * (Probe[(hk - 1) * ProbesPerStep] * scale), tb = pos + rot * (Probe[hk * ProbesPerStep] * scale);
            Vector3 d = tb - ta; float len = d.magnitude;
            float frac = 1f;                                  // a miss means the touch is at the far probe (the detection used r + EnterPad)
            RaycastHit hit;
            if (len > 1e-5f && Physics.SphereCast(ta, r + EnterPad, d / len, out hit, len, herMask, QueryTriggerInteraction.Ignore))
            {
                frac = Mathf.Clamp01(hit.distance / len);
                if (segOf != null && hit.collider != null) segIndex = segOf(hit.collider);
            }
            if (segIndex < 0) segIndex = SegAt(hp, r + EnterPad, herMask, segOf);
            return CurlAt(hk - 1) + frac * (CurlAt(hk) - CurlAt(hk - 1));
        }

        static int SegAt(Vector3 p, float radius, int herMask, Func<Collider, int> segOf)
        {
            if (segOf == null) return -1;
            int n = Physics.OverlapSphereNonAlloc(p, radius, _buf, herMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n && i < _buf.Length; i++)
            {
                if (_buf[i] == null) continue;
                int s = segOf(_buf[i]);
                _buf[i] = null;
                if (s >= 0) return s;
            }
            return -1;
        }
    }
}
