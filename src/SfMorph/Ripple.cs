using System;
using System.Collections.Generic;
using UnityEngine;

namespace SfMorph
{
    // 0.8.0 ripple shockwave (design 4): impacts (SoftSim's fresh fast contacts via SoftZones.Impacts, Reactions' slaps via Impact) become
    // <= 8 expanding damped rings that GpuContact's Compose kernel adds to her soft skin, plus a short pulse on the breast DynamicBones.
    internal static class Ripple
    {
        public const int MaxWaves = 8;
        public static string Status = "";

        // Live waves right now.
        public static int Count { get { return 0; } }

        // Main thread, every frame after Simulate (Body.ApplyAll): reads z.Impacts, ages the waves, services the breast pulses.
        public static void Update(Character her, SoftZones z, ContactSettings s, float now) { }

        // An impact from outside the soft zones (Reactions: a slap). speed m/s, dir = into her (world), bone = the wave follows it.
        public static void Impact(Vector3 point, Vector3 dir, float speed, Transform bone, string by) { }

        // GPU upload (GpuContact.Run): per live wave two Vector4s: [2i] = (origin world xyz, age s), [2i+1] = (A m, c m/s, lambda m, tau s).
        // arr.Length >= 2 * MaxWaves. Returns the wave count.
        public static int Fill(float now, Vector4[] arr) { return 0; }

        public static void Release() { Status = ""; }
    }
}
