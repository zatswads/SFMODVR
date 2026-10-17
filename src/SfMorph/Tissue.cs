using UnityEngine;

namespace SfMorph
{
    // 0.8.0: how much flesh there is to press, by body part, for one slim-average woman (her rig at its default proportions). One table
    // shared by the GPU dent (ContactBake caps per vertex), the soft zones (SoftSim: per-zone stiffness, depth and damping) and the hands'
    // resistance, so a press sinks and pushes back by the same anatomy everywhere:
    // - Hard: skin and the thin layer over bone that any press compresses (mm): shin, sternum, iliac crest, skull, hands, feet are 1-4 mm.
    // - Soft: the compressible fat and relaxed muscle on top of that at Softness 50 % (mm). Subcutaneous fat of young women (calliper /
    //   ultrasound ranges): glutes 20-30 mm, lower belly 15-25, inner/back thigh 15-20, front/outer thigh 10-15 (fascia lata keeps it firm),
    //   triceps ~10, biceps ~5, calf 8-10, forearm 4-5; breasts are mostly fat and glandular tissue on the chest wall. A firm palm press
    //   compresses roughly half to two thirds of that; the visible dent is Hard + Soft x the part's share.
    // - Floor: the share of Soft a vertex of this class gets outside the soft-zone areas (bone-near parts of the class stay firmer).
    // Progressive resistance: flesh is soft first and stiffens as it compresses (fat lobules flatten, then fascia and muscle take the
    // load). Depth follows the press exactly up to Knee x cap, then saturates smoothly toward the cap (Press); the resistance a hand feels
    // rises faster than linearly with how compressed the part is (Resist), so a fingertip sinks easily into a glute and stops quickly on a shin.
    internal static class Tissue
    {
        // bone classes (design 1.3): 0 none, 1 pelvis, 2 spine, 3 chest, 4 neck, 5 head, 6/7 thigh, 8/9 calf, 10/11 foot, 12/13 upper arm,
        // 14/15 forearm, 16/17 hand, 18/19 butt, 20/21 breast, 22 belly, 23 genital, 24 scapula (even = left for paired classes)
        public const int Classes = 25;
        // 0.11.7: head 1+1 -> 1.5+3 mm and neck soft 2 -> 3 mm (cheeks and the side of the neck give a little; the face dents at all now)
        public static readonly float[] HardMm =  { 2f, 3f, 3f, 2f, 2f, 1.5f, 4f, 4f, 3f, 3f, 1f, 1f, 3f, 3f, 2f, 2f, 1f, 1f, 5f, 5f, 4f, 4f, 4f, 3f, 2f };
        public static readonly float[] SoftMm =  { 0f, 6f, 4f, 2f, 3f, 3f, 12f, 12f, 6f, 6f, 1f, 1f, 7f, 7f, 3f, 3f, 1f, 1f, 18f, 18f, 20f, 20f, 14f, 6f, 2f };
        public static readonly float[] Floor =   { 0f, .4f, .5f, .5f, .5f, .5f, .5f, .5f, .6f, .6f, .3f, .3f, .6f, .6f, .5f, .5f, .5f, .5f, .7f, .7f, .8f, .8f, .7f, .6f, .4f };
        // shin and front of the upper arm: a calf/upper-arm vertex facing forward (bind pose) sits over the tibia / the thin biceps layer
        public const float ShinFloor = 0.1f, BicepsFloor = 0.45f;
        public const float Knee = 0.6f;

        // Softness 0..1 -> how much of Soft a press reaches (0.6 firm .. 1.4 soft; 1 at the default 50 %).
        public static float SoftScale(float softness) { return Mathf.Lerp(0.6f, 1.4f, Mathf.Clamp01(softness)); }

        // The deepest dent of a vertex (m): Hard + Soft x scale x max(zone share, floor), x depth (ContactSettings.Depth).
        public static float Cap(int cls, float zoneShare, float floorOverride, float softness, float depth)
        {
            if (cls < 0 || cls >= Classes) cls = 0;
            float fl = floorOverride >= 0f ? floorOverride : Floor[cls];
            return (HardMm[cls] + SoftMm[cls] * SoftScale(softness) * Mathf.Max(Mathf.Clamp01(zoneShare), fl)) * 0.001f * Mathf.Max(0.1f, depth);
        }

        // Shown depth for a penetration `pen` against `cap` (both m): exact up to Knee x cap, then a smooth (C1) saturation toward cap.
        // The same curve is in SoftDeform.compute (Press).
        public static float Press(float pen, float cap)
        {
            if (!(pen > 0f) || !(cap > 0f)) return 0f;
            float k = Knee * cap;
            if (pen <= k) return pen;
            float r = cap - k;
            return k + r * Tanh((pen - k) / r);
        }

        // 0..1 resistance of compressed flesh: halfway between linear and (dent / cap)^2 (soft first, stiff near the cap, but a hand is felt
        // from the first millimetres: pure squared let a physics hand sink half the depth at almost full force).
        public static float Resist(float dent, float cap)
        {
            if (!(cap > 0f)) return 1f;
            float f = Mathf.Clamp01(dent / cap);
            return 0.5f * (f + f * f);
        }

        static float Tanh(float x) { float e = Mathf.Exp(-2f * Mathf.Abs(x)); float t = (1f - e) / (1f + e); return x < 0f ? -t : t; }

        // Soft zones (SoftZones ids, by prefix): spring stiffness, dent depth and damping relative to the global Softness/Springiness values.
        // breast: softest, deepest, least damped (it jiggles); glute: deep and soft; belly: medium, shallower (abdominal wall under it);
        // inner thigh: soft; outer thigh: firm and shallow (fascia lata, vastus lateralis).
        // 0.11.37: this frame's Chubby fat per area (mm, signed; ContactSettings.FatMm x Chubby, filled by Body), for the soft zones' physics
        public static readonly float[] AreaFatMm = new float[ContactSettings.FatAreas];

        // The fat (mm) under a soft zone: glutes -> the glutes' area, belly -> belly, inner / outer thigh -> thighs; breasts have their own.
        public static float ZoneFatMm(string id)
        {
            if (id == null) return 0f;
            if (id.StartsWith("glute")) return AreaFatMm[3];
            if (id.StartsWith("belly")) return AreaFatMm[0];
            if (id.StartsWith("thigh")) return AreaFatMm[4];
            return 0f;
        }

        public static void Zone(string id, out float kMul, out float limitMul, out float dampMul)
        {
            kMul = 1f; limitMul = 1f; dampMul = 1f;
            if (id == null) return;
            if (id.StartsWith("breast")) { kMul = 0.6f; limitMul = 1.0f; dampMul = 0.7f; }
            else if (id.StartsWith("glute")) { kMul = 0.8f; limitMul = 1.0f; dampMul = 0.9f; }
            else if (id.StartsWith("belly")) { kMul = 1.0f; limitMul = 0.75f; dampMul = 1.1f; }
            else if (id.StartsWith("thighIn")) { kMul = 0.9f; limitMul = 0.8f; dampMul = 1.0f; }
            else if (id.StartsWith("thighOut")) { kMul = 1.4f; limitMul = 0.55f; dampMul = 1.2f; }
        }
    }
}
