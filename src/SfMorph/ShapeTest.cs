using UnityEngine;

namespace SfMorph
{
    // 0.11.32 Debug tab tests for 0.11.28-0.11.31's shape layers, like the Flex test: each runs over whatever she is doing and gives her pose
    // and sliders back when it ends.
    //  - Fold test: the inner elbows, the backs of the knees, the groin in turn at full depth (whatever the joint angle), FoldStep s each.
    //  - Chubby test: each area alone at full (its mm, at least 10 mm), then all of them slimmer (-100 %) and all of them fuller (+100 %).
    //  - Mush A/B: Delta Mush switched on and off every MushHalf s for MushRun s (watch a bent joint).
    internal static class ShapeTest
    {
        public const float FoldStep = 2.5f, FatStep = 2f, MushHalf = 1f, MushRun = 10f;
        public static readonly string[] FoldNames = { "inner elbows", "backs of the knees", "groin" };
        public static readonly string[] FatNames = { "belly", "waist", "back", "glutes", "thighs", "arms", "calves", "face", "all slimmer (-100 %)", "all fuller (+100 %)" };
        static float _fold = -1f, _fat = -1f, _mush = -1f;
        static int _foldLogged = -1, _fatLogged = -1;

        public static void StartFold() { _fold = Time.time; _foldLogged = -1; _fat = -1f; }
        public static void StartFat() { _fat = Time.time; _fatLogged = -1; _fold = -1f; }
        public static void StartMush() { _mush = Time.time; }
        public static void Stop() { _fold = _fat = _mush = -1f; }

        static int StepOf(ref float start, float step, int n)
        {
            if (start < 0f) return -1;
            int i = (int)((Time.time - start) / step);
            if (i < 0 || i >= n) { start = -1f; return -1; }
            return i;
        }

        public static int FoldIndex { get { return StepOf(ref _fold, FoldStep, FoldNames.Length); } }
        public static int FatIndex { get { return StepOf(ref _fat, FatStep, FatNames.Length); } }
        public static float FoldLeft { get { return _fold < 0f ? 0f : FoldStep - (Time.time - _fold) % FoldStep; } }
        public static float FatLeft { get { return _fat < 0f ? 0f : FatStep - (Time.time - _fat) % FatStep; } }
        public static bool MushRunning { get { if (_mush >= 0f && Time.time - _mush > MushRun) _mush = -1f; return _mush >= 0f; } }
        // null = no A/B running; else whether Delta Mush is on right now
        public static bool? MushOverride { get { if (!MushRunning) return null; return ((int)((Time.time - _mush) / MushHalf) & 1) == 0; } }

        // BendPose: the fold slots of the step at full depth (the settings' mm, x SkinFolds but at least x1), the others 0
        public static bool FillFolds(ContactSettings s, float[] amt)
        {
            int i = FoldIndex;
            if (i < 0) { _foldLogged = -1; return false; }
            float k = Mathf.Max(1f, s.FoldAmount);
            float mm = i == 0 ? s.ElbowFoldMm : i == 1 ? s.KneeFoldMm : s.HipFoldMm;
            for (int j = 0; j < amt.Length; j++) amt[j] = j / 2 == i ? Mathf.Max(2f, mm) * 0.001f * k : 0f;
            if (_foldLogged != i) { _foldLogged = i; Plugin.Log.LogInfo("Shape test: fold " + FoldNames[i] + " at " + (Mathf.Max(2f, mm) * k).ToString("0.0") + " mm for " + FoldStep.ToString("0.0") + " s"); }
            return true;
        }

        // GpuContact: the areas' fat (m) of the step, or false (use the sliders)
        public static bool FillFat(ContactSettings s, float[] mOut)
        {
            int i = FatIndex;
            if (i < 0) { _fatLogged = -1; return false; }
            int n = ContactSettings.FatAreas;
            for (int a = 0; a < n; a++)
            {
                float mm = Mathf.Max(10f, s.FatMm[a]);
                mOut[a] = i < n ? (a == i ? mm : 0f) : i == n ? -s.FatMm[a] : s.FatMm[a];
                mOut[a] *= 0.001f;
            }
            if (_fatLogged != i)
            {
                _fatLogged = i;
                Plugin.Log.LogInfo("Shape test: chubby " + FatNames[i] + (i < n ? " at " + Mathf.Max(10f, s.FatMm[i]).ToString("0") + " mm" : "") + " for " + FatStep.ToString("0.0") + " s");
            }
            return true;
        }
    }
}
