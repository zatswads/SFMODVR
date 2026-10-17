using System;
using System.Collections.Generic;
using UnityEngine;

namespace SfMorph
{
    // Penetration (0.11.25): how deep a finger / hand capsule is in her vagina, anus or mouth this frame. HerBody.GeoPush calls Begin once
    // per frame (her animation pose, before Overlap writes) and Feed for every source it feeds her segments: your hand models, the
    // partner hands it accepts, the physics hands' ghosts (the CONTROLLER pose: the PhysX fingers stop at her skin, the ghost goes on in)
    // and Embody's hands at the IK target. Reactions reads the result (strokes, sounds, excitement).
    // Each opening is a short tunnel: a centre O on the opening's bones, an inward axis N (toward the *_inside / tongue bones, else her
    // pelvis root / head), a radius R from the opening's bones. A capsule point counts when it lies within R + its radius of the axis and
    // no deeper than MaxDepth; Depth = the deepest such point along N (negative: still outside, within Near of the opening).
    internal static class Penetration
    {
        public const int Vagina = 0, Anus = 1, Mouth = 2, NO = 3;
        public static readonly string[] Names = { "vagina", "anus", "mouth" };
        const float MaxDepth = 0.16f, Near = 0.03f, MaxCapR = 0.013f;   // fingers and thumbs (9-12.5 mm); a palm (15 mm and up) never goes in
        const int Samples = 5;

        // this frame's result, valid while Frame == Time.frameCount; Depth = float.NaN: nothing near
        public static readonly float[] Depth = new float[NO];
        public static readonly string[] By = new string[NO];
        public static readonly int[] Hand = new int[NO];
        public static readonly Vector3[] O = new Vector3[NO], N = new Vector3[NO];
        public static readonly bool[] Ok = new bool[NO];
        public static int Frame = -1;
        public static string Status = "";

        static readonly float[] _r = new float[NO];
        static readonly Transform[][] _open = new Transform[NO][], _in = new Transform[NO][];
        static readonly Transform[] _base = new Transform[NO];
        static Transform _forRoot; static int _forBuild = int.MinValue;

        static readonly string[] VagOpen = { "Left_Vaginal", "Right_Vaginal", "Upper_Vaginal", "Lower_Vaginal" };
        static readonly string[] VagIn = { "Left_Vaginal_inside", "Right_Vaginal_inside", "Upper_Vaginal_inside", "Lower_Vaginal_inside" };
        static readonly string[] MouthOpen = { "Lip_Left_Inside", "Lip_Right_Inside", "Lip_Upper_Inside", "Lip_Lower_Insite", "Lip_LeftUpper_Inside", "Lip_RightUpper_Inside", "Lip_LeftLower_Insite", "Lip_RightLower_Insite" };

        // Her opening bones, found once per her / per HerBody build (one transform walk, not per frame).
        static void Resolve(Transform root)
        {
            _forRoot = root; _forBuild = HerBody.BuildVersion;
            for (int k = 0; k < NO; k++) { _open[k] = new Transform[0]; _in[k] = new Transform[0]; _base[k] = null; }
            if (root == null) { Status = "off: she is not built"; return; }
            var by = new Dictionary<string, Transform>(StringComparer.Ordinal);
            var rect = new List<Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.name.StartsWith(SoftZones.Prefix, StringComparison.Ordinal)) continue;
                if (!by.ContainsKey(t.name)) by[t.name] = t;
                // the anal ring: Rectum_0..8, Rectum_L_*, Rectum_R_* under Rectum_root (not Rectum_Top, not the root itself)
                if (t.name.StartsWith("Rectum_", StringComparison.Ordinal) && t.name != "Rectum_root" && t.name != "Rectum_Top") rect.Add(t);
            }
            Func<string[], Transform[]> all = names => { var l = new List<Transform>(); foreach (var n in names) { Transform t; if (by.TryGetValue(n, out t)) l.Add(t); } return l.ToArray(); };
            Transform rm, head, tongue, rr;
            by.TryGetValue("Root_M", out rm); by.TryGetValue("Head_M", out head); by.TryGetValue("Tongue", out tongue); by.TryGetValue("Rectum_root", out rr);
            _open[Vagina] = all(VagOpen); _in[Vagina] = all(VagIn); _base[Vagina] = rm;
            _open[Anus] = rect.Count > 0 ? rect.ToArray() : rr != null ? new[] { rr } : new Transform[0]; _base[Anus] = rm;
            _open[Mouth] = all(MouthOpen); _in[Mouth] = tongue != null ? new[] { tongue } : new Transform[0]; _base[Mouth] = head;
            var sb = new System.Text.StringBuilder("Penetration: openings ");
            for (int k = 0; k < NO; k++)
                sb.Append(k > 0 ? ", " : "").Append(Names[k]).Append(' ').Append(_open[k].Length).Append(" bones").Append(_in[k].Length > 0 ? " (+" + _in[k].Length + " inside)" : "");
            Status = sb.ToString();
            Plugin.Log.LogInfo(Status);
        }

        static bool Mean(Transform[] ts, out Vector3 c)
        {
            c = Vector3.zero; int n = 0;
            for (int i = 0; i < ts.Length; i++) if (ts[i] != null) { c += ts[i].position; n++; }
            if (n == 0) return false;
            c /= n; return true;
        }

        // Once per frame, before the Feeds: the openings where her pose has them now.
        public static void Begin()
        {
            Frame = Time.frameCount;
            for (int k = 0; k < NO; k++) { Depth[k] = float.NaN; By[k] = null; Hand[k] = -1; Ok[k] = false; }
            var root = HerBody.Root;
            if (!ReferenceEquals(root, _forRoot) || HerBody.BuildVersion != _forBuild) Resolve(root);
            if (root == null) return;
            for (int k = 0; k < NO; k++)
            {
                Vector3 c, inn;
                if (!Mean(_open[k], out c)) continue;
                Vector3 n = Vector3.zero;
                if (Mean(_in[k], out inn) && (inn - c).sqrMagnitude > 0.003f * 0.003f) n = inn - c;
                else if (_base[k] != null) n = _base[k].position - c;
                if (n.sqrMagnitude < 1e-6f) continue;
                n.Normalize();
                // the radius: the opening bones' spread across the axis (a closed mouth still takes a finger), clamped
                float sp = 0f;
                for (int i = 0; i < _open[k].Length; i++) if (_open[k][i] != null) sp = Mathf.Max(sp, Vector3.ProjectOnPlane(_open[k][i].position - c, n).magnitude);
                _r[k] = k == Mouth ? Mathf.Clamp(sp * 0.8f, 0.012f, 0.03f) : k == Vagina ? Mathf.Clamp(sp * 0.6f, 0.012f, 0.03f) : Mathf.Clamp(sp + 0.004f, 0.01f, 0.025f);
                O[k] = c; N[k] = n; Ok[k] = true;
            }
        }

        // One source capsule (world, this frame). by/hand: who, for the log and the reactions.
        public static void Feed(Vector3 a, Vector3 b, float rad, string by, int hand)
        {
            if (Frame != Time.frameCount || !(rad > 0f) || rad > MaxCapR) return;
            for (int k = 0; k < NO; k++)
            {
                if (!Ok[k]) continue;
                Vector3 o = O[k], n = N[k];
                float lim = _r[k] + rad, best = float.NaN;
                for (int i = 0; i < Samples; i++)
                {
                    Vector3 p = Vector3.Lerp(a, b, i / (float)(Samples - 1)) - o;
                    float d = Vector3.Dot(p, n);
                    if (d < -Near || d > MaxDepth) continue;
                    if ((p - n * d).sqrMagnitude > lim * lim) continue;
                    if (float.IsNaN(best) || d > best) best = d;
                }
                if (float.IsNaN(best)) continue;
                if (float.IsNaN(Depth[k]) || best > Depth[k]) { Depth[k] = best; By[k] = by; Hand[k] = hand; }
            }
        }
    }
}
