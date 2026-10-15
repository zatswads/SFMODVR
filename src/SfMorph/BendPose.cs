using System;
using System.Collections.Generic;
using UnityEngine;

namespace SfMorph
{
    // 0.8.0 bend correctives (design 2.2): her elbow, knee and hip angles vs the bind pose -> 8 bulge amplitudes (metres) that GpuContact's
    // Compose kernel adds per vertex by ContactBake.Bend weights. 9 slots (uniforms _BendAmt0..2): 0/1 biceps L/R (elbow), 2/3 thigh back L/R
    // (knee), 4/5 thigh top L/R (hip flexion), 6/7 calf L/R (knee), 8 lower-belly fold (both hips flexed; its vertices carry a negative weight).
    // Amplitudes are >= 0 metres, already x BendAmount; the per-vertex weight's sign says bulge out (+) or fold in (-).
    internal static class BendPose
    {
        public const int Slots = 9;
        public static readonly float[] Amt = new float[Slots];
        public static bool Any;
        public static string Status = "";

        // Her joints from the character's bones (call when her bones change; cheap, no per-frame lookups after it).
        public static void Bind(Character her) { }

        // Once per frame before the GPU chain (GpuContact.Run).
        public static void Update(ContactSettings s) { Any = false; }

        public static void Release() { Any = false; for (int i = 0; i < Amt.Length; i++) Amt[i] = 0f; Status = ""; }
    }
}
