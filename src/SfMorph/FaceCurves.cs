using System;
using UnityEngine;

namespace SfMorph
{
    // 0.11.47: the ARKit-style curve weights her face is driven by this frame, for the curve-driven wrinkles (Wrinkles.UpdateActs), the way
    // MetaHuman drives its wrinkle maps from the rig's controls instead of from the mesh. Two producers, no allocation per frame:
    //  - the lip sync (Reactions.LipFrame): the Audio2Face weights it writes into her mouth (LipSync.Shapes, x its fade), also the mouth pose
    //    editor's preview;
    //  - her face clips (ReactionsFace.ClipStep / SurpriseStep): the clips are bone tracks with no curves, so each mood gives an estimate (a
    //    preset of curves typical of that expression) x the clip's envelope weight.
    // A curve's value = the larger of the two. Producers that stop writing go stale after StaleAfter s (read as 0).
    internal static class FaceCurves
    {
        // the lip sync's shapes first (same indices as LipSync.Shapes), then the eye shapes only the mood presets give
        public static readonly string[] Names = BuildNames();
        public static readonly int LipK = LipSync.K;
        public static readonly int N = Names.Length;
        const float StaleAfter = 0.25f;

        static string[] BuildNames()
        {
            var extra = new[] { "eyeSquintLeft", "eyeSquintRight", "eyeBlinkLeft", "eyeBlinkRight", "eyeWideLeft", "eyeWideRight" };
            var a = new string[LipSync.K + extra.Length];
            Array.Copy(LipSync.Shapes, a, LipSync.K);
            Array.Copy(extra, 0, a, LipSync.K, extra.Length);
            return a;
        }

        public static int Index(string n)
        {
            for (int i = 0; i < Names.Length; i++) if (string.Equals(Names[i], n, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        // ---- producers ----

        static readonly float[] _lip = new float[LipSync.K];
        static float _lipFade, _lipAt = -100f;
        static readonly float[] _moodW = new float[ReactionsFace.NM];
        static readonly float[] _moodAt = new float[ReactionsFace.NM];

        // The lip sync's weights this frame (0..1 each) and its fade (0..1).
        public static void Lip(float[] w, float fade)
        {
            if (w == null) return;
            int n = Math.Min(w.Length, _lip.Length);
            for (int i = 0; i < n; i++) _lip[i] = w[i];
            _lipFade = Mathf.Clamp01(fade);
            _lipAt = Time.unscaledTime;
        }

        // A face clip of mood m at weight w (its envelope x weight) this frame.
        public static void Mood(int m, float w)
        {
            if (m < 0 || m >= ReactionsFace.NM) return;
            float now = Time.unscaledTime;
            // two writers of one mood in a frame (the clip and the built surprise): the larger
            _moodW[m] = now - _moodAt[m] < 1e-4f ? Mathf.Max(_moodW[m], Mathf.Clamp01(w)) : Mathf.Clamp01(w);
            _moodAt[m] = now;
        }

        // ---- the mood presets (an estimate: what each expression typically does, ARKit 0..1 at full weight) ----

        static float[][] _preset;
        static float[][] Presets()
        {
            if (_preset != null) return _preset;
            var p = new float[ReactionsFace.NM][];
            for (int m = 0; m < p.Length; m++) p[m] = new float[N];
            // moaning / feeling: worried brows, soft squint; stronger with the level
            Set(p[ReactionsFace.Pleasure1], "browInnerUp", 0.45f, "eyeSquint", 0.25f, "mouthSmile", 0.15f, "cheekSquint", 0.1f);
            Set(p[ReactionsFace.Pleasure2], "browInnerUp", 0.6f, "eyeSquint", 0.4f, "cheekSquint", 0.25f, "jawOpen", 0.2f, "mouthSmile", 0.1f);
            Set(p[ReactionsFace.Pleasure3], "browInnerUp", 0.75f, "eyeSquint", 0.55f, "cheekSquint", 0.4f, "noseSneer", 0.25f, "browDown", 0.2f, "jawOpen", 0.3f);
            // laughing (the grief brows go on top of it)
            Set(p[ReactionsFace.Ticklish], "mouthSmile", 0.85f, "cheekSquint", 0.75f, "eyeSquint", 0.6f, "browInnerUp", 0.5f, "jawOpen", 0.3f, "mouthDimple", 0.3f);
            Set(p[ReactionsFace.Surprise], "browInnerUp", 0.9f, "browOuterUp", 0.9f, "eyeWide", 0.8f, "jawOpen", 0.3f);
            Set(p[ReactionsFace.Pain], "browDown", 0.7f, "browInnerUp", 0.5f, "eyeSquint", 0.75f, "cheekSquint", 0.55f, "noseSneer", 0.55f,
                "mouthFrown", 0.45f, "mouthStretch", 0.5f, "mouthUpperUp", 0.3f, "mouthShrugLower", 0.3f);
            Set(p[ReactionsFace.Climax], "browInnerUp", 0.85f, "eyeSquint", 0.6f, "eyeBlink", 0.6f, "cheekSquint", 0.5f, "noseSneer", 0.35f,
                "mouthStretch", 0.35f, "jawOpen", 0.45f, "browDown", 0.2f);
            _preset = p;
            return p;
        }

        // pairs name, value; a name without a side sets both sides when the curve is split (eyeSquint -> eyeSquintLeft / Right)
        static void Set(float[] row, params object[] kv)
        {
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                string n = (string)kv[i]; float v = (float)kv[i + 1];
                int k = Index(n);
                if (k >= 0) { row[k] = v; continue; }
                int l = Index(n + "Left"), r = Index(n + "Right");
                if (l >= 0) row[l] = v;
                if (r >= 0) row[r] = v;
                if (l < 0 && r < 0) Plugin.Log.LogWarning("Wrinkles: face curve preset names an unknown curve '" + n + "'");
            }
        }

        // ---- reader ----

        // All N curve values now into w (length >= N); returns true when any producer is live. No allocation.
        public static bool Read(float[] w)
        {
            float now = Time.unscaledTime;
            bool any = false;
            Array.Clear(w, 0, N);
            if (now - _lipAt < StaleAfter && _lipFade > 0f)
            {
                any = true; _seen |= 1;
                for (int i = 0; i < LipK; i++) w[i] = _lip[i] * _lipFade;
            }
            var p = Presets();
            for (int m = 0; m < _moodW.Length; m++)
            {
                if (now - _moodAt[m] >= StaleAfter || _moodW[m] <= 0f) continue;
                any = true; _seen |= 2 << m;
                float mw = _moodW[m];
                if (mw > _seenW[m]) _seenW[m] = mw;
                var row = p[m];
                for (int i = 0; i < N; i++) { float v = row[i] * mw; if (v > w[i]) w[i] = v; }
            }
            return any;
        }

        // What drove her face since the last call (for the wrinkles' peak log): "lip sync, Pain up to 80%" ...; clears it.
        static int _seen;
        static readonly float[] _seenW = new float[ReactionsFace.NM];
        public static string TakeSeen()
        {
            string s = (_seen & 1) != 0 ? "lip sync" : "";
            for (int m = 0; m < _seenW.Length; m++)
                if ((_seen & (2 << m)) != 0) s += (s.Length > 0 ? ", " : "") + ReactionsFace.MoodNames[m] + " up to " + (_seenW[m] * 100f).ToString("0") + "%";
            _seen = 0;
            Array.Clear(_seenW, 0, _seenW.Length);
            return s.Length > 0 ? s : "nothing";
        }
    }
}
