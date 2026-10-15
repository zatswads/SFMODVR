using UnityEngine;

namespace SfMorph
{
    // The Reactions tab (0.7.9). R0: the master toggle and the status; the rest comes with the director.
    internal partial class Panel
    {
        void ReactionsTab(Character c)
        {
            GUILayout.Label("Reactions", _bold);
            // what the gating reads is taken before any control here changes the config: the same controls in the Layout pass and the click pass
            bool apply = _p.Apply.Value, physics = _p.Physics.Value, handsOn = _p.HandsOn.Value;
            string who = !c.Female || c.IsPlayer ? "only for the game's woman"
                       : !apply ? "'Apply changes' is off"
                       : !physics && !handsOn ? "soft zones and physics hands are both off (no hand is tracked against her)" : null;
            GUI.enabled = who == null;
            bool on = GUILayout.Toggle(_p.ReactOn.Value, " She reacts to touch: caress, grope, slaps (voice, face, flinch, slap sound, blush)");
            GUI.enabled = true;
            if (who == null && on != _p.ReactOn.Value) _p.ReactOn.Value = on;
            if (who != null) { GUILayout.Label("    off: " + who, _off); return; }
            string status = _body.React.Status;
            if (_p.ReactOn.Value && status.Length > 0) GUILayout.Label(status, status.StartsWith("off") ? _off : _hint);
        }
    }
}
