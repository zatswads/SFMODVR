using UnityEngine;

namespace SfMorph
{
    // Analytic two-bone IK (root -> mid -> end), the same solve as Embody.Place (his arms) without the controller plumbing. Used by
    // Overlap's planted lock: the animation's end-effector position is the target, the CURRENT (lagged) mid joint gives the pole, so
    // the lag of the chain shows as the elbow/knee swinging while the hand/foot stays where the animation put it.
    internal static class Ik
    {
        // 1, not Embody's 0.98: the target is the animation's own end position with the animation's own bone lengths, so it is always
        // reachable; a 0.98 clamp bent every planted near-straight leg/arm (knee 8 cm off the line) with nothing touching her. The law of
        // cosines below is clamped to [-1, 1], so a fully straight chain is safe.
        const float ReachClamp = 1f;

        // Rotates root and mid (world rotations, slerped by w from what they are now toward the solved ones) so that `end` reaches
        // `target`, and slerps end.rotation toward endRot by w. w 0 = untouched, 1 = exact (up to ReachClamp). false = nothing done.
        // 0.8.0: the pole rotated about dir (unit) by degrees (Embody's elbow swivel). Stub until P2.
        public static Vector3 SwivelPole(Vector3 pole, Vector3 dir, float degrees) { return pole; }

        public static bool TwoBone(Transform root, Transform mid, Transform end, Vector3 target, Quaternion endRot, float w)
        {
            if (root == null || mid == null || end == null || !(w > 0f)) return false;
            if (float.IsNaN(target.x) || float.IsNaN(target.y) || float.IsNaN(target.z)) return false;
            w = Mathf.Min(w, 1f);
            Vector3 A = root.position, B = mid.position, C = end.position;
            float l1 = (B - A).magnitude, l2 = (C - B).magnitude;
            if (l1 < 1e-4f || l2 < 1e-4f) return false;

            var toT = target - A;
            float dist = toT.magnitude;
            float d = Mathf.Clamp(dist, Mathf.Max(Mathf.Abs(l1 - l2), 1e-4f), (l1 + l2) * ReachClamp);
            var dir = dist > 1e-5f ? toT / dist : (C - A).normalized;
            var Tc = A + dir * d;

            float cosA = Mathf.Clamp((l1 * l1 + d * d - l2 * l2) / (2f * l1 * d), -1f, 1f);
            float sinA = Mathf.Sqrt(1f - cosA * cosA);
            var pole = (B - A) - dir * Vector3.Dot(B - A, dir);       // where the mid joint is now, off the root-target line
            if (pole.sqrMagnitude < 1e-6f) pole = Vector3.down - dir * Vector3.Dot(Vector3.down, dir);   // a straight chain: bend down
            if (pole.sqrMagnitude < 1e-6f) pole = Vector3.Cross(dir, Vector3.right);
            pole.Normalize();
            var B2 = A + dir * (l1 * cosA) + pole * (l1 * sinA);

            var r0 = root.rotation;
            var r1 = FromTo(B - A, B2 - A) * r0;
            root.rotation = w >= 0.9999f ? r1 : Quaternion.Slerp(r0, r1, w);
            B = mid.position; C = end.position;                       // moved with the root
            var m0 = mid.rotation;
            var m1 = FromTo(C - B, Tc - B) * m0;
            mid.rotation = w >= 0.9999f ? m1 : Quaternion.Slerp(m0, m1, w);
            end.rotation = w >= 0.9999f ? endRot : Quaternion.Slerp(end.rotation, endRot, w);
            return true;
        }

        static Quaternion FromTo(Vector3 a, Vector3 b)
        {
            if (a.sqrMagnitude < 1e-10f || b.sqrMagnitude < 1e-10f) return Quaternion.identity;
            return Quaternion.FromToRotation(a, b);
        }
    }
}
