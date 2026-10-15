using UnityEngine;

namespace SfMorph
{
    // The Reactions tab (0.7.9): the master toggle, what she reacts with, her states, test buttons and the dirty-talk voice guard.
    internal partial class Panel
    {
        // panel-local state (not saved)
        int _testZone = 4, _testSide;     // Butt, left
        bool _expOpen;
        int _expMood, _expReaction;

        static readonly string[] VoiceModes = { "Game picks", "By intensity" };
        static readonly string[] SideNames = { "L", "R" };
        static readonly string[] TierNames = { "Caress", "Grope", "Soft slap", "Hard slap", "Too hard" };
        // Zones whose hit has a side: Breast, Butt, Thigh, Calf, Arm, Face. Chest, Belly, Hips and Neck are single.
        static readonly bool[] ZoneHasSide = { true, false, false, false, true, true, true, true, true, false };

        void ReactionsTab(Character c)
        {
            GUILayout.Label("Reactions", _bold);
            // what the gating reads is taken before any control here changes the config: the same controls in the Layout pass and the click pass
            bool apply = _p.Apply.Value, physics = _p.Physics.Value, handsOn = _p.HandsOn.Value;
            string who = !c.Female || c.IsPlayer ? "only for the game's woman"
                       : !apply ? "'Apply changes' is off"
                       : !physics && !handsOn ? "soft zones and physics hands are both off (no hand is tracked against her)" : null;
            bool reactOn = _p.ReactOn.Value;
            bool voice = _p.ReactVoice.Value, sound = _p.ReactSound.Value, blush = _p.ReactBlush.Value;
            bool face = _p.ReactFace.Value, flinch = _p.ReactFlinch.Value, blushBody = _p.ReactBlushBody.Value;
            bool gaze = _p.ReactGaze.Value, feed = _p.ReactFeedDesire.Value;
            bool auto = _p.ReactAuto.Value, allowClimax = _p.ReactClimax.Value, guard = _p.VoiceGuard.Value;
            int voiceMode = Mathf.Clamp(_p.ReactVoiceMode.Value, 0, 1);
            float sensCfg = _p.ReactSensitivity.Value, gapCfg = _p.ReactCooldown.Value, volCfg = _p.ReactVolume.Value;
            string sub = who ?? (reactOn ? null : "reactions are off");
            string guardOff = !c.Female || c.IsPlayer ? "only for the game's woman" : null;   // the guard needs neither the toggle nor touch tracking
            int zone = Mathf.Clamp(_testZone, 0, Reactions.ZoneNames.Length - 1);
            int side = Mathf.Clamp(_testSide, 0, 1);
            bool zoneSides = ZoneHasSide[zone];
            bool expOpen = _expOpen;
            Reactions react = _body.React;
            string status = react.Status, stateLine = react.StateLine, bars = react.Bars, guardStatus = react.GuardStatus;
            float e = react.E, p = react.P;

            GUI.enabled = who == null;
            bool on = GUILayout.Toggle(reactOn, " She reacts to touch: caress, grope, slaps (voice, face, flinch, slap sound, blush)");
            GUI.enabled = true;
            if (who == null && on != reactOn) _p.ReactOn.Value = on;
            if (who != null) { GUILayout.Label("    off: " + who, _off); GameVoiceBlock(guardOff, guard, guardStatus); return; }
            if (reactOn && status.Length > 0) GUILayout.Label(status, status.StartsWith("off") ? _off : _hint);

            float sens = Slider("Sensitivity", sensCfg * 100f, 0f, 100f, "0", 50f, null, " %") / 100f;
            if (Mathf.Abs(sens - sensCfg) > 0.0001f) _p.ReactSensitivity.Value = sens;
            GUILayout.Label("0: only hard slaps count; 100: light taps too. Soft slap from 1.2 m/s, hard from 2.3, too hard from 4.5 (at 50 %).", _hint);
            float gap = Slider("Voice gap", gapCfg, 0.5f, 5f, "0.0", 1.5f, null, " s");
            if (Mathf.Abs(gap - gapCfg) > 0.0001f) _p.ReactCooldown.Value = gap;
            float vol = Slider("Slap volume", volCfg * 100f, 0f, 100f, "0", 60f, sound ? null : "off: Sound is off", " %") / 100f;
            if (sound && Mathf.Abs(vol - volCfg) > 0.0001f) _p.ReactVolume.Value = vol;

            // what she reacts with
            GUI.enabled = sub == null;
            GUILayout.BeginHorizontal();
            bool tv = GUILayout.Toggle(voice, " Voice", GUILayout.Width(90));
            bool tf = GUILayout.Toggle(face, " Face", GUILayout.Width(90));
            bool tl = GUILayout.Toggle(flinch, " Flinch", GUILayout.Width(90));
            bool ts = GUILayout.Toggle(sound, " Sound", GUILayout.Width(90));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            bool tb = GUILayout.Toggle(blush, " Blush", GUILayout.Width(90));
            GUI.enabled = sub == null && blush;
            bool tbb = GUILayout.Toggle(blushBody, " Body blush", GUILayout.Width(110));
            GUI.enabled = sub == null;
            bool tg = GUILayout.Toggle(gaze, " Gaze away", GUILayout.Width(110));
            bool tfd = GUILayout.Toggle(feed, " Feed the game's desire");
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            if (sub == null)
            {
                if (tv != voice) _p.ReactVoice.Value = tv;
                if (tf != face) _p.ReactFace.Value = tf;
                if (tl != flinch) _p.ReactFlinch.Value = tl;
                if (ts != sound) _p.ReactSound.Value = ts;
                if (tb != blush) _p.ReactBlush.Value = tb;
                if (blush && tbb != blushBody) _p.ReactBlushBody.Value = tbb;
                if (tg != gaze) _p.ReactGaze.Value = tg;
                if (tfd != feed) _p.ReactFeedDesire.Value = tfd;
            }
            else GUILayout.Label("    off: " + sub, _off);
            GUILayout.Label("Face: a wince or blink when there is no voice. Gaze away: she looks away for a moment after a hard hit. " +
                            "Feed: touch also raises the game's own desire gauge.", _hint);

            GUI.enabled = sub == null && voice;
            GUILayout.Label("Voice", _hint);
            int vm = GUILayout.Toolbar(voiceMode, VoiceModes);
            GUI.enabled = true;
            if (sub == null && voice && vm != voiceMode) _p.ReactVoiceMode.Value = vm;
            GUILayout.Label("Game picks: the game's own moan. By intensity: a moan chosen from her voice library by how hard the touch was.", _hint);

            // States
            GUILayout.Label("States", _bold);
            if (sub != null) GUILayout.Label("    off: " + sub, _off);
            else
            {
                if (stateLine.Length > 0) GUILayout.Label(stateLine, _hint);
                if (bars.Length > 0) GUILayout.Label(bars, _hint);
            }
            GUI.enabled = sub == null;
            bool nauto = GUILayout.Toggle(auto, " States change by themselves (auto)");
            bool nclimax = GUILayout.Toggle(allowClimax, " Allow climax at full excitement (cosmetic: three moans, then afterglow)");
            if (sub == null)
            {
                if (nauto != auto) _p.ReactAuto.Value = nauto;
                if (nclimax != allowClimax) _p.ReactClimax.Value = nclimax;
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Idle")) react.SetState(RState.Idle, 0f, 0f);
            if (GUILayout.Button("Aroused 1")) react.SetState(RState.Aroused, 0.3f, p);
            if (GUILayout.Button("Aroused 2")) react.SetState(RState.Aroused, 0.5f, p);
            if (GUILayout.Button("Aroused 3")) react.SetState(RState.Aroused, 0.7f, p);
            if (GUILayout.Button("Aroused 4")) react.SetState(RState.Aroused, 0.9f, p);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Startled")) react.SetState(RState.Startled, e, p);
            if (GUILayout.Button("Guarded")) react.SetState(RState.Guarded, 0.1f, 0.6f);
            GUI.enabled = sub == null && allowClimax;
            if (GUILayout.Button("Climax")) react.SetState(RState.Climax, 1f, 0f);
            GUI.enabled = sub == null;
            if (GUILayout.Button("Afterglow")) react.SetState(RState.Afterglow, 0.25f, 0f);
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            if (sub == null && !allowClimax) GUILayout.Label("    Climax: off: 'Allow climax' is not ticked", _off);

            // Test
            GUILayout.Label("Test a reaction", _bold);
            if (sub != null) GUILayout.Label("    off: " + sub, _off);
            GUI.enabled = sub == null;
            int nz = GUILayout.SelectionGrid(zone, Reactions.ZoneNames, 5);
            GUI.enabled = sub == null && zoneSides;
            int ns = GUILayout.Toolbar(side, SideNames, GUILayout.Width(120));
            GUI.enabled = sub == null;
            if (sub == null)
            {
                if (nz != zone) _testZone = nz;
                if (zoneSides && ns != side) _testSide = ns;
            }
            GUILayout.BeginHorizontal();
            for (int t = 0; t < TierNames.Length; t++)
                if (GUILayout.Button(TierNames[t])) react.Test(zone, zoneSides ? side : 0, t + 1);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Moan")) react.TestResponse(0);
            if (GUILayout.Button("Breath")) react.TestResponse(1);
            if (GUILayout.Button("Wince")) react.TestResponse(2);
            if (GUILayout.Button("Slap sound")) react.TestResponse(3);
            if (GUILayout.Button("Flinch butt")) react.TestResponse(4);
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            GUILayout.Label("A test is a real reaction at the zone's live position: same voice rules, cooldowns and excitement.", _hint);

            // Experimental
            if (GUILayout.Button((expOpen ? "- " : "+ ") + "Experimental", _bold)) _expOpen = !_expOpen;
            if (expOpen)
            {
                GUILayout.Label("The game's own animator numbers, set once per press. What they mean is unknown: try them and watch her.", _hint);
                GUI.enabled = sub == null;
                int mood = Stepper("mood_num", _expMood);
                int rn = Stepper("reaction_num", _expReaction);
                GUI.enabled = true;
                if (sub == null && mood != _expMood)
                {
                    _expMood = mood;
                    _body.React.SetAnimFloat("mood_num", mood);
                }
                if (sub == null && rn != _expReaction)
                {
                    _expReaction = rn;
                    _body.React.SetAnimFloat("reaction_num", rn);
                }
            }

            GameVoiceBlock(guardOff, guard, guardStatus);
            GUILayout.Label("Caress: a hand resting or stroking (about a quarter second, light). Grope: pressing hard (20 N or 1.5 cm deep). " +
                            "Slaps: by how fast the hand arrives (soft 1.2, hard 2.3, too hard 4.5 m/s at 50 % sensitivity). " +
                            "Excitement (tier 0 to 4) rises with touch and fades after ten quiet seconds; pain rises with hard slaps. " +
                            "Flinches need the 'Push her limbs' slider above 0 and soft zones or physics hands.", _hint);
        }

        // The game's dirty-talk voice error repair: independent of the reactions toggle, only needs her.
        void GameVoiceBlock(string whoOff, bool guard, string guardStatus)
        {
            GUILayout.Label("Game voice", _bold);
            GUI.enabled = whoOff == null;
            bool g = GUILayout.Toggle(guard, " Repair the game's dirty-talk voice error (IndexOutOfRange every frame)");
            GUI.enabled = true;
            if (whoOff == null && g != guard) _p.VoiceGuard.Value = g;
            if (whoOff != null) { GUILayout.Label("    off: " + whoOff, _off); return; }
            if (guard && guardStatus.Length > 0) GUILayout.Label(guardStatus, guardStatus.StartsWith("off") ? _off : _hint);
        }

        // A label with - / + buttons and the value; returns the new value.
        int Stepper(string label, int v)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(170));
            if (GUILayout.Button("-", GUILayout.Width(32))) v--;
            GUILayout.Label(v.ToString(), GUILayout.Width(40));
            if (GUILayout.Button("+", GUILayout.Width(32))) v++;
            GUILayout.EndHorizontal();
            return v;
        }
    }
}
