using UnityEngine;

namespace SfMorph
{
    // 0.11.47 the quality presets' row (Quality.cs): at the top of the Physics tab (with what differs from the pick) and in the Debug tab
    // beside the timing (compact). A click writes the preset's settings, also on the picked one (back from Custom).
    internal partial class Panel
    {
        bool _qualityDiffOpen;
        string _qTip, _qTipNext;   // the hovered preset's hint: read in Repaint, shown from the next Layout on (both passes draw the same text)

        void QualitySection(bool compact)
        {
            if (Event.current.type == EventType.Layout) _qTip = _qTipNext;
            int cur = Mathf.Clamp(_p.QualityPreset.Value, 0, QualityPresets.Count - 1);
            int diff = QualityPresets.Differences(cur);
            if (!compact) GUILayout.Label("Quality preset", _bold);
            GUILayout.BeginHorizontal();
            int pick = -1;
            for (int i = 0; i < QualityPresets.Count; i++)
            {
                bool on = cur == i && diff == 0;
                if (GUILayout.Toggle(on, QualityPresets.Button(i), GUI.skin.button) != on) pick = i;
            }
            GUILayout.Toggle(diff > 0, "Custom", GUI.skin.button, GUILayout.Width(70));   // a marker, not a choice: what Custom is comes from the others
            GUILayout.EndHorizontal();
            if (pick >= 0) QualityPresets.Apply(_p, pick, "panel");
            // the hovered button's hint, else the pick's (GUI.tooltip: the hovered control's tooltip, only ours count)
            if (Event.current.type == EventType.Repaint)
            {
                string t = GUI.tooltip;
                _qTipNext = null;
                for (int i = 0; i < QualityPresets.Count; i++) if (ReferenceEquals(t, QualityPresets.Hints[i]) || t == QualityPresets.Hints[i]) _qTipNext = QualityPresets.Hints[i];
            }
            string tip = _qTip;
            string name = QualityPresets.Names[cur];
            if (!string.IsNullOrEmpty(tip)) GUILayout.Label(tip, _hint);
            else if (diff > 0) GUILayout.Label("Custom: " + diff + (diff == 1 ? " setting differs" : " settings differ") + " from " + name + " (changed by hand). Click " + name + " to set "
                                               + (diff == 1 ? "it" : "them") + " back, or pick another preset.", _off);
            else GUILayout.Label(QualityPresets.Hints[cur], _hint);
            if (compact) return;
            GUILayout.Label(QualityPresets.Note, _hint);
            if (diff > 0)
            {
                if (GUILayout.Button((_qualityDiffOpen ? "- " : "+ ") + "What differs from " + name, _fold)) _qualityDiffOpen = !_qualityDiffOpen;
                if (_qualityDiffOpen) GUILayout.Label(QualityPresets.DiffText(cur), _hint);
            }
            if (GUILayout.Button((_qualityCostOpen ? "- " : "+ ") + "Cost settings (the presets set these too)", _fold)) _qualityCostOpen = !_qualityCostOpen;
            if (_qualityCostOpen) CostRows();
        }

        static readonly string[] LipFilters = { "Moment", "Average", "Average + closures" };
        bool _qualityCostOpen;

        // the settings that were constants before 0.11.47 (the rest of what a preset covers has its row in its own section)
        void CostRows()
        {
            IntRow("  Soft-zone steps / frame", _p.SoftMaxSteps, 4, 16, 8);
            float v = Slider("  GPU contact cull", _p.ContactCullDistance.Value, 0f, 20f, "0.0", 3f, null, " m");
            if (Mathf.Abs(v - _p.ContactCullDistance.Value) > 0.001f) _p.ContactCullDistance.Value = v;
            IntRow("  Self-contact hold", _p.SelfHitHold, 10, 120, 30, " fr");
            IntRow("  Wrinkle re-blend gap", _p.WrinkRasterGap, 1, 6, 2, " fr");
            v = Slider("  Wrinkles idle after", _p.WrinkAsleepAfter.Value, 0.1f, 2f, "0.00", 0.5f, null, " s");
            if (Mathf.Abs(v - _p.WrinkAsleepAfter.Value) > 0.0001f) _p.WrinkAsleepAfter.Value = v;
            v = Slider("  Muscle re-blend every", _p.MuscleBlendInterval.Value, 0.03f, 0.5f, "0.00", 0.15f, null, " s");
            if (Mathf.Abs(v - _p.MuscleBlendInterval.Value) > 0.0001f) _p.MuscleBlendInterval.Value = v;
            IntRow("  Face-clip track rate", _p.FaceTrackRate, 10, 60, 15, " fps");
            IntRow("  Face-clip samples / frame", _p.FaceTrackBudget, 1, 16, 6);
            IntRow("  Hull cooks / frame", _p.HandsCookPerFrame, 1, 4, 2);
            IntRow("  Hand solver iterations", _p.HandsSolverIters, 4, 32, 16);
            GUILayout.Label("Lip sync: how the curve is read each frame", _hint);
            int lf = GUILayout.Toolbar(Mathf.Clamp(_p.LipFilter.Value, 0, 2), LipFilters);
            if (lf != _p.LipFilter.Value) _p.LipFilter.Value = lf;
            bool lead = GUILayout.Toggle(_p.LipAutoLead.Value, " Lip sync leads by one frame (the picture reaches you a frame after the sound)");
            if (lead != _p.LipAutoLead.Value) _p.LipAutoLead.Value = lead;
            GUILayout.Label("Soft-zone steps: 1/120 s springs per frame; below 120 / steps fps the jiggle slows down. Cull: her meshes out of view or farther than this " +
                            "skip their dents, bends, muscles and Delta Mush (a pressing hand or a slap still runs). Self-contact hold: frames her own limbs keep the dent " +
                            "chain on after they were found pressing. Re-blend gap / interval: how often the wrinkle and muscle textures are redrawn. Face-clip tracks: " +
                            "samples per second (applies when a track is next built) and per frame while building. Hulls / solver: physics hands (solver from the next " +
                            "hand build). Lip filter: Average + closures never misses a p / b / m at a low frame rate.", _hint);
        }

        void IntRow(string label, BepInEx.Configuration.ConfigEntry<int> e, int min, int max, int neutral, string unit = "")
        {
            int v = Mathf.RoundToInt(Slider(label, e.Value, min, max, "0", neutral, null, unit));
            if (v != e.Value) e.Value = v;
        }
    }
}
