using System;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace SfMorph
{
    // 0.11.47 quality presets (user: one place to trade the look for frame rate; the knob inventory of 17 Oct). A preset is a shortcut like
    // the build triangle (Panel.Build.cs): picking one writes the real config entries it covers, nothing else, and every setting stays free
    // afterwards. [General] QualityPreset keeps the last pick; the panel shows Custom while a covered setting differs from it. Never covered:
    // her look (sizes, bend bulges, folds, chubby, flex, ripple, the lip look sliders) and the skin marks (frozen: docs/LESSONS.md).
    // Nothing is written at start: a config from before this version keeps its values until a preset is picked.
    internal static class QualityPresets
    {
        public const int Count = 4;
        public static readonly string[] Names = { "Performance", "Balanced", "Quality", "Best quality" };
        // what each one gains and what it costs, in plain words (the panel shows it under the row and as the buttons' tooltips)
        public static readonly string[] Hints =
        {
            "Performance: for the lowest frame rates. Few soft points, no Delta Mush, her own limbs don't dent her, no wrinkles or crease shade, " +
            "smaller wrinkle / muscle textures re-blended less often, simpler physics hands (capsule colliders, posed fingers), GPU contact only within 2 m. " +
            "Gains the most GPU and CPU time; costs: joints pinch in deep bends, no expression lines, flatter contacts.",
            "Balanced (default, ~23 fps VR): dense soft points, Delta Mush at 15 mm, self-contact, face and neck wrinkles at 2048 with crease shade, " +
            "full physics hands, GPU contact within 3 m. Most of the look for a moderate cost.",
            "Quality: Balanced plus the body creases, wrinkle textures at the skin's full size, face clips sampled at 30 fps (smoother fast expressions), " +
            "Delta Mush at 20 mm, muscles re-blended more often, contact within 4 m. Sharper detail for more GPU memory and time, mostly in the wrinkle textures.",
            "Best quality: the best look whatever it costs. Fidelity soft points (her body subdivided: ~4x the vertices in every GPU pass), Delta Mush at 25 mm, " +
            "a wider dent edge and smoother dent shading, wrinkles re-blended every frame, 12 soft-zone steps per frame, every breast collider, contact within 8 m. " +
            "Expect the lowest frame rate.",
        };
        public const string Note = "Picking a preset writes these settings now; a Soft points change rebuilds her soft zones (a 1-3 s hitch). " +
                                   "Her look (sizes, bulges, folds, chubby, flex, ripple, lip look) and the skin marks are never changed by a preset.";

        static readonly GUIContent[] _buttons = new GUIContent[Count];
        public static GUIContent Button(int i) { return _buttons[i]; }

        const float N = float.NaN;   // not set by that preset (the user's value stays and never counts as a difference)

        // one covered setting: per preset its value (P, B, Q, BQ); Kind 1 = an on / off by its value: 0 = off, > 0 = on (a user's own
        // strength above 0 is kept, a 0 is raised to the preset's value)
        sealed class Knob
        {
            public string Label;
            public float[] V;
            public Func<float> Get;
            public Action<float> Set;
            public float Eps;
            public int Kind;
            public string Fmt = "0.##";

            public bool Matches(int preset)
            {
                float v = V[preset];
                if (float.IsNaN(v)) return true;
                float cur = Get();
                if (Kind == 1) return v <= 0f ? cur <= 0f : cur > 0f;
                return Mathf.Abs(cur - v) <= Eps;
            }
        }

        static Knob[] _k = new Knob[0];
        public static int KnobCount { get { return _k.Length; } }
        public static int LastWritten = -1;   // settings the last Apply wrote (the smoke test: re-applying the picked preset writes 0)
        static bool _applying;
        static Plugin _p;

        static Knob F(string label, ConfigEntry<float> e, float eps, float p, float b, float q, float bq, int kind = 0, string fmt = "0.##")
        {
            return new Knob { Label = label, V = new[] { p, b, q, bq }, Get = () => e.Value, Set = x => e.Value = x, Eps = eps, Kind = kind, Fmt = fmt };
        }
        static Knob I(string label, ConfigEntry<int> e, float p, float b, float q, float bq)
        {
            return new Knob { Label = label, V = new[] { p, b, q, bq }, Get = () => e.Value, Set = x => e.Value = Mathf.RoundToInt(x), Eps = 0.5f, Fmt = "0" };
        }
        static Knob B(string label, ConfigEntry<bool> e, float p, float b, float q, float bq)
        {
            return new Knob { Label = label, V = new[] { p, b, q, bq }, Get = () => e.Value ? 1f : 0f, Set = x => e.Value = x > 0.5f, Eps = 0.5f, Fmt = "on;;off" };
        }

        // Called once by Plugin.Awake after every entry is bound. The order is the write order: the Delta Mush radius before Quality (a
        // Quality change rebuilds her soft zones, and that rebuild's bake reads the radius).
        public static void Init(Plugin p)
        {
            _p = p;
            for (int i = 0; i < Count; i++) _buttons[i] = new GUIContent(Names[i], Hints[i]);
            _k = new[]
            {
                F("Delta Mush radius (mm)", p.DeltaMushRadius, 0.5f, N, 15f, 20f, 25f, 0, "0"),
                I("Soft points (0 Performance, 1 Balanced, 2 Fidelity)", p.Quality, 0, 1, 1, 2),
                I("Soft-zone steps per frame", p.SoftMaxSteps, 8, 8, 8, 12),
                F("Dent edge (m)", p.ContactSkirt, 0.0005f, 0.010f, 0.015f, 0.015f, 0.020f, 0, "0.000"),
                F("Dent shading smoothness (mm)", p.ContactNormalSmooth, 0.25f, 5f, 8f, 8f, 10f),
                B("Round joints (Delta Mush)", p.DeltaMush, 0, 1, 1, 1),
                B("Her own limbs press her skin", p.SelfContact, 0, 1, 1, 1),
                I("Self-contact hold (frames)", p.SelfHitHold, 20, 30, 30, 30),
                F("GPU contact cull distance (m)", p.ContactCullDistance, 0.05f, 2f, 3f, 4f, 8f, 0, "0.0"),
                I("Breast physics colliders (0 off, 1 main, 2 all)", p.BreastColliders, 1, 1, 1, 2),
                B("Wrinkles", p.WrinkOn, 0, 1, 1, 1),
                B("Wrinkles on her face", p.WrinkFace, N, 1, 1, 1),
                B("Wrinkles on her neck", p.WrinkNeck, N, 1, 1, 1),
                B("Wrinkles at her joints (body)", p.WrinkBody, N, 0, 1, 1),
                I("Wrinkle texture size (0 full, 1 2048, 2 1024)", p.WrinkResolution, 2, 1, 0, 0),
                F("Crease shade", p.WrinkCavity, 0.001f, 0f, 0.3f, 0.3f, 0.3f, 1),
                F("Wrinkle cull distance (m)", p.WrinkCullDistance, 0.05f, 1.5f, 2.5f, 3f, 5f, 0, "0.0"),
                B("Lines follow the face curves", p.WrinkCurveDrive, 1, 1, 1, 1),
                I("Wrinkle re-blend gap (frames)", p.WrinkRasterGap, 3, 2, 2, 1),
                F("Wrinkles idle after (s)", p.WrinkAsleepAfter, 0.01f, 0.3f, 0.5f, 0.5f, 0.5f),
                F("Muscle re-blend interval (s)", p.MuscleBlendInterval, 0.005f, 0.25f, 0.15f, 0.1f, 0.05f, 0, "0.00"),
                I("Muscle driver (1 lengths + loads)", p.MuscleDriver, 1, 1, 1, 1),
                I("Face-clip track rate (fps)", p.FaceTrackRate, 15, 15, 30, 30),
                I("Face-clip samples per frame", p.FaceTrackBudget, 4, 6, 6, 8),
                I("Lip filter (2 average + closures)", p.LipFilter, 2, 2, 2, 2),
                B("Lip sync leads by a frame", p.LipAutoLead, 1, 1, 1, 1),
                B("Hull colliders (physics hands)", p.HandsHull, 0, 1, 1, 1),
                I("Hulls cooked per frame", p.HandsCookPerFrame, 1, 2, 2, 2),
                B("Jointed fingers (physics hands)", p.HandsJointed, 0, 1, 1, 1),
                I("Hand solver iterations", p.HandsSolverIters, 8, 16, 16, 16),
            };
        }

        // How many covered settings differ from the preset (0 = it is exactly that preset). Allocation-free: the panel calls it every repaint.
        public static int Differences(int preset)
        {
            if (preset < 0 || preset >= Count) return 0;
            int n = 0;
            var k = _k;
            for (int i = 0; i < k.Length; i++) if (!k[i].Matches(preset)) n++;
            return n;
        }

        // "label: now x, preset y" per difference (the panel's foldout; built only while it is open)
        public static string DiffText(int preset)
        {
            if (preset < 0 || preset >= Count) return "";
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            foreach (var k in _k)
            {
                if (k.Matches(preset)) continue;
                float v = k.V[preset], cur = k.Get();
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("  ").Append(k.Label).Append(": now ").Append(cur.ToString(k.Fmt, ci)).Append(", ").Append(Names[preset]).Append(' ')
                  .Append(k.Kind == 1 ? (v > 0f ? "on" : "off") : v.ToString(k.Fmt, ci));
            }
            return sb.ToString();
        }

        // Writes the preset's values into the config entries (the systems read them every frame or at their next build) and remembers the
        // pick. Only values that differ are written, so a re-pick after one slider moved changes just that slider back.
        public static void Apply(Plugin p, int preset, string why)
        {
            if (_applying || p == null || _k.Length == 0) return;
            preset = Mathf.Clamp(preset, 0, Count - 1);
            _applying = true;
            // one save of the config file at the end instead of one per setting (BepInEx writes the whole file on every set)
            var cf = p.Config;
            bool saveEach = cf.SaveOnConfigSet;
            cf.SaveOnConfigSet = false;
            try
            {
                int q0 = p.Quality.Value;
                float r0 = p.DeltaMushRadius.Value;
                if (p.QualityPreset.Value != preset) p.QualityPreset.Value = preset;   // its SettingChanged comes back here: _applying
                int n = 0;
                foreach (var k in _k)
                {
                    if (k.Matches(preset)) continue;
                    float v = k.V[preset];
                    if (k.Kind == 1 && v > 0f && k.Get() > 0f) continue;   // on already, at the user's own strength
                    k.Set(v);
                    n++;
                }
                // the radius is read when her body meshes are baked: a Quality change rebuilds (and bakes with the new radius), otherwise ask
                // GPU contact to bake the Delta Mush data again
                bool radius = Mathf.Abs(p.DeltaMushRadius.Value - r0) > 0.01f;
                if (radius && p.Quality.Value == q0) GpuContact.MushRadiusAsk = true;
                LastWritten = n;
                Plugin.Log.LogInfo("Quality preset " + Names[preset] + " (" + why + "): " + (n == 0 ? "nothing to change" : n + " settings written")
                                   + (p.Quality.Value != q0 ? ", soft points rebuild" : radius ? ", Delta Mush baked again" : ""));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Quality preset " + preset + ": " + e.Message); }
            finally
            {
                cf.SaveOnConfigSet = saveEach;
                try { if (saveEach) cf.Save(); } catch (Exception e) { Plugin.Log.LogWarning("Quality preset: config not saved: " + e.Message); }
                _applying = false;
            }
        }
    }
}
