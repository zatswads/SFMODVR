using System;
using UnityEngine;

namespace SfMorph
{
    // 0.8.0 (design-0.8.0 section 5.2): a finger's curl sweep, fixed to the palm: the tip and the last capsule's midpoint at Steps curl
    // fractions, from forward kinematics of the rest pose, built once. "First fraction that touches" is the stop curl: open-loop (it
    // depends only on the palm pose, never on where the finger is now), so closing the finger can't change what it senses.
    // Stub until P1: Build fills nothing yet, Conform* return 1 (nothing touched).
    internal sealed class FingerFan
    {
        public const int Steps = 8;                                          // curl fractions 0, 1/7, ..., 1
        public const int ProbesPerStep = 2;                                  // [0] the tip, [1] the midpoint of the last capsule
        public readonly Vector3[] Probe = new Vector3[Steps * ProbesPerStep];   // in the PALM (wrist) frame, metres
        public float R;                                                      // probe radius (metres)

        // joints 2 (physics hand: proximal + fused) or 3 (his hand). P: joint origins [0..joints-1] + the tip [joints], palm frame.
        // Q: rest rotations of the joints in the palm frame. axisL: curl axis in each joint's own frame. maxDeg: curl at c = 1.
        public void Build(int joints, Vector3[] P, Quaternion[] Q, Vector3[] axisL, float[] maxDeg, float r) { R = r; }

        public static float CurlAt(int step) { return (float)step / (Steps - 1); }

        // analytic variant (Embody): the fan placed at (pos, rot, scale), tested against HerBody's skin shapes. The contact curl in [0, 1]
        // (1 = nothing touched up to full curl) and the segment hit (-1 none).
        public float ConformAnalytic(Vector3 pos, Quaternion rot, float scale, out int segIndex) { segIndex = -1; return 1f; }

        // physics variant (physics hands): CheckSphere on `herMask`, enter radius R + enterPad, stay radius R - exitPad on the probe that
        // touched last time (`prevStep`, -1 none). `segOf` maps the collider hit to a segment.
        public float ConformPhysics(Vector3 pos, Quaternion rot, float scale, int herMask, int prevStep, Func<Collider, int> segOf,
                                    out int segIndex, out int step)
        {
            segIndex = -1; step = -1; return 1f;
        }
    }
}
