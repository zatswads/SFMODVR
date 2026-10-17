using UnityEngine;

namespace SfMorph
{
    // 0.11.12 (user: all the test and debug stuff should live in its own tab): timing, the GPU check, the soft-point markers, the wrinkle /
    // muscle debug views, the Flex test and every Reactions test (reactions, sounds, face, lip sync, diagnose, voice dump). The other tabs keep
    // only what changes how she looks or behaves.
    internal partial class Panel
    {
        void DebugTab(Character c)
        {
            string who = !c.Female || c.IsPlayer ? "only for the game's woman" : !_p.Apply.Value ? "'Apply changes' is off" : null;

            // timing
            GUILayout.Label("Timing (last 10 s)", _bold);
            if (!_p.PerfLog.Value) GUILayout.Label("    off: [General] PerfLog is off", _off);
            else GUILayout.Label(Perf.Summary.Length > 0 ? Perf.Summary : "the first line comes after 10 s", _hint);
            // 0.11.47: the quality presets beside the numbers they move (Panel.Quality.cs)
            QualitySection(true);
            if (_body.Gpu != null && _p.GpuContactOn.Value)
                GUILayout.Label("GPU contact: " + _body.Gpu.ActiveRenderers + " renderers working, " + _body.Gpu.CulledRenderers + " culled (out of view"
                                + (_p.ContactCullDistance.Value > 0f ? " or beyond " + _p.ContactCullDistance.Value.ToString("0.0") + " m)" : ")"), _hint);

            // physics / contact
            GUILayout.Label("Physics and contact", _bold);
            string gpuOff = who ?? (!_p.GpuContactOn.Value ? "GPU contact is off (Physics tab)" : _body.Gpu == null ? "shader bundle not loaded" : null);
            GUI.enabled = gpuOff == null;
            if (GUILayout.Button("GPU check (3 s)", GUILayout.Width(160))) _body.Gpu.Smoke(3f, 0.01f);
            GUI.enabled = true;
            GUILayout.Label(gpuOff != null ? "    off: " + gpuOff : "Sinks all of her skin 1 cm for 3 s, to see that the per-vertex dents run (a deformation is the expected result).", gpuOff != null ? _off : _hint);
            string softOff = who ?? (_p.Physics.Value ? null : "soft zones are off");
            GUI.enabled = softOff == null;
            bool show = GUILayout.Toggle(_p.ShowSoftPoints.Value, " Show soft points and contact capsules");
            GUI.enabled = true;
            if (softOff == null && show != _p.ShowSoftPoints.Value) _p.ShowSoftPoints.Value = show;

            // skin
            GUILayout.Label("Skin", _bold);
            string wOff = who ?? (_p.WrinkOn.Value ? null : "wrinkles are off");
            GUILayout.Label("Wrinkles view" + (wOff != null ? " (off: " + wOff + ")" : ""), _hint);
            GUI.enabled = wOff == null;
            int wd = GUILayout.Toolbar(Mathf.Clamp(_p.WrinkDebug.Value, 0, 2), WrinkleDebugs);
            GUI.enabled = true;
            if (wOff == null && wd != _p.WrinkDebug.Value) _p.WrinkDebug.Value = wd;
            string mOff = who ?? (_p.MuscleOn.Value ? null : "muscles are off");
            GUILayout.Label("Muscles view" + (mOff != null ? " (off: " + mOff + ")" : ""), _hint);
            GUI.enabled = mOff == null;
            int md = GUILayout.Toolbar(Mathf.Clamp(_p.MuscleDebug.Value, 0, 2), MuscleDebugs);
            GUI.enabled = true;
            if (mOff == null && md != _p.MuscleDebug.Value) _p.MuscleDebug.Value = md;
            // 0.11.23 Wrinkle test: each expression's lines at full in turn, apart from how far her face compresses the skin
            int wt = WrinkleTest.Index;
            GUILayout.BeginHorizontal();
            GUI.enabled = wOff == null;
            if (wt < 0)
            {
                if (GUILayout.Button("Wrinkle test (brows, frown, squint, smile, nose, frown lips, whole face, neck, body: " + WrinkleTest.Step.ToString("0.0") + " s each)")) WrinkleTest.Start();
            }
            else if (GUILayout.Button("Stop test", GUILayout.Width(90))) WrinkleTest.Stop();
            GUI.enabled = true;
            if (wt >= 0) GUILayout.Label("now: " + WrinkleTest.Names[wt] + " at full (" + WrinkleTest.Left.ToString("0.0") + " s)", _hint);
            GUILayout.EndHorizontal();
            if (wOff != null) GUILayout.Label("    Wrinkle test off: " + wOff, _off);
            else GUILayout.Label("    Full lines at Strength " + _p.WrinkStrength.Value.ToString("0.0") + ", Cavity " + _p.WrinkCavity.Value.ToString("0.0")
                                 + ". Faint here = the lines themselves (raise Strength / Cavity); clear here but faint in play = her expressions don't compress the skin enough (lower Threshold). Now: "
                                 + (_body.WrinkleStatus.Length > 0 ? _body.WrinkleStatus : "-"), _hint);
            // Flex test: arms, back, core, legs in turn at full over her current animation (needs GPU contact, not the relief)
            var gpu = _body.Gpu;
            string flexOff = who ?? (!_p.GpuContactOn.Value ? "GPU contact is off (Physics tab)" : gpu == null ? "shader bundle not loaded"
                           : !gpu.MuscleKernels ? "the shader bundle is older than this DLL (install dist/sfmorph_shaders)"
                           : gpu.MuscleVerts == 0 ? (gpu.FlexNote.Length > 0 ? gpu.FlexNote : "no muscle on her baked body meshes (yet)") : null);
            string testing = MuscleFlex.TestArea();
            GUILayout.BeginHorizontal();
            GUI.enabled = flexOff == null;
            if (testing == null)
            {
                if (GUILayout.Button("Flex test (arms, back, core, legs: 2 s each)")) MuscleFlex.StartTest();
            }
            else if (GUILayout.Button("Stop test", GUILayout.Width(90))) MuscleFlex.StopTest();
            GUI.enabled = true;
            if (testing != null) GUILayout.Label("now: " + testing + " at full (" + MuscleFlex.TestLeft(testing).ToString("0.0") + " s)", _hint);
            GUILayout.EndHorizontal();
            if (flexOff != null) GUILayout.Label("    Flex test off: " + flexOff, _off);
            ShapeTests();

            HapticTests();
            ReactionTests(c);
        }

        // 0.11.26: one test per actuator (0.8 for 0.5 s), a slap pulse, a rub buzz, strokes; they play with Haptics off too
        void HapticTests()
        {
            GUILayout.Label("Haptics", _bold);
            bool any = Haptics.Path.Length > 0;
            GUI.enabled = any;
            for (int h = 0; h < 2; h++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(h == 0 ? "Left" : "Right", GUILayout.Width(50));
                for (int a = 0; a < Haptics.NA; a++) if (GUILayout.Button(Haptics.ActNames[a])) Haptics.TestActuator(h, a);
                if (GUILayout.Button("slap")) Haptics.TestSlap(h);
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Rub buzz (1.5 s)")) Haptics.TestRub();
            if (GUILayout.Button("Strokes, right trigger (2.5 s)")) Haptics.TestStrokes();
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            GUILayout.Label(!any ? "    off: no haptics path found yet (" + Haptics.Status + ")"
                                 : "    Path: " + Haptics.Path + (Haptics.Localized ? "" : ". One motor: trigger / thumb tests play on the main motor."), any ? _hint : _off);
        }

        void ReactionTests(Character c)
        {
            GUILayout.Label("Reactions", _bold);
            Reactions react = _body.React;
            bool handsTracked = _p.Physics.Value || _p.HandsOn.Value;
            string who = !c.Female || c.IsPlayer ? "only for the game's woman" : !_p.Apply.Value ? "'Apply changes' is off"
                       : !handsTracked ? "soft zones and physics hands are both off (no hand is tracked against her)" : null;
            string sub = who ?? (_p.ReactOn.Value ? null : "reactions are off (Reactions tab)");
            int zone = Mathf.Clamp(_testZone, 0, Reactions.ZoneNames.Length - 1);
            int side = Mathf.Clamp(_testSide, 0, 1);
            bool zoneSides = ZoneHasSide[zone];
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
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Line for the zone")) react.TestLine(zone, zoneSides ? side : 0);
            if (GUILayout.Button("Feeling sound")) react.TestResponse(5);
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            GUILayout.Label("A test is a real reaction at the zone's live position: same voice rules, cooldowns and excitement.", _hint);

            // skin sounds
            string skinOff = sub ?? (!_p.ReactSound.Value ? "Sound is off" : !_p.ReactFoley.Value ? "skin sounds are off" : null);
            GUI.enabled = skinOff == null;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Test pat")) react.TestResponse(6);
            if (GUILayout.Button("Test grab")) react.TestResponse(8);
            if (GUILayout.Button("Test rub (1.5 s)")) react.TestResponse(7);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Test penetration sound")) react.TestResponse(9);
            if (GUILayout.Button("Test penetration stroke")) react.TestResponse(10);
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            if (skinOff != null) GUILayout.Label("    skin sounds off: " + skinOff, _off);

            // face
            bool face = _p.ReactFace.Value;
            string faceOff = sub ?? (!face ? "Face is off" : null);
            GUI.enabled = faceOff == null;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Eye Lv1")) react.TestEye(1);
            if (GUILayout.Button("Eye Lv2")) react.TestEye(2);
            if (GUILayout.Button("Eye Lv3")) react.TestEye(3);
            if (GUILayout.Button("Blink")) react.TestBlink();
            GUILayout.EndHorizontal();
            var fc = react.Face;
            string moodOff = faceOff ?? (!_p.ReactFaceClips.Value ? "face clips are off" : !fc.Surveyed ? "her face survey is not done yet" : null);
            GUI.enabled = moodOff == null;
            GUILayout.BeginHorizontal();
            for (int m = 0; m < ReactionsFace.NM; m++)
                if (GUILayout.Button(ReactionsFace.MoodNames[m])) react.TestFace(m);
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            if (moodOff != null) GUILayout.Label("    face moods off: " + moodOff, _off);

            // lip sync
            string lipOff = moodOff ?? (!_p.LipSyncOn.Value ? "lip sync is off" : !fc.LipReady ? "lip sync is not ready" : null);
            GUI.enabled = lipOff == null;
            if (GUILayout.Button("Test: a pack clip with lip sync")) react.TestLip();
            GUI.enabled = true;
            if (lipOff != null) GUILayout.Label("    off: " + lipOff, _off);

            // the game's animator numbers
            if (GUILayout.Button((_expOpen ? "- " : "+ ") + "Experimental", _fold)) _expOpen = !_expOpen;
            if (_expOpen)
            {
                GUILayout.Label("The game's own animator numbers, set once per press. What they mean is unknown: try them and watch her.", _hint);
                GUI.enabled = sub == null;
                int mood = Stepper("mood_num", _expMood);
                int rn = Stepper("reaction_num", _expReaction);
                GUI.enabled = true;
                if (sub == null && mood != _expMood) { _expMood = mood; react.SetAnimFloat("mood_num", mood); }
                if (sub == null && rn != _expReaction) { _expReaction = rn; react.SetAnimFloat("reaction_num", rn); }
            }

            // one-off probe of the sound and face chain (writes a block to LogOutput.log)
            bool bridge = react.BridgeFound, diagBusy = react.DiagBusy, diagRan = react.DiagRan;
            string guardOff = !c.Female || c.IsPlayer ? "only for the game's woman" : null;
            if (guardOff != null) return;
            GUI.enabled = bridge && !diagBusy;
            if (GUILayout.Button(diagBusy ? "Diagnosing (about 3 s)..." : "Diagnose sound and face")) react.Diagnose();
            GUI.enabled = true;
            if (!bridge) GUILayout.Label("    off: her game link is not found yet (turn on Reactions or the voice repair and stay in the game scene)", _off);
            else if (diagBusy) GUILayout.Label("Listen for two short beeps and a slap; she may wince once.", _hint);
            else if (diagRan) GUILayout.Label("Diagnose ran: see LogOutput.log, 'Reactions diagnose'", _hint);
            else GUILayout.Label("Plays a test tone and one slap clip, sets the moan trigger once, and logs what the audio and face chain looks like.", _hint);

            // 0.9.9: the game's voice clips to WAV, for baking lip-sync curves (tools/a2f)
            bool dumping = react.Dumping;
            GUI.enabled = bridge && !dumping;
            if (GUILayout.Button(dumping ? "Dumping voice clips..." : "Dump her voice clips to WAV (for lip sync)")) react.DumpVoice();
            GUI.enabled = true;
            string ds = react.DumpStatus;
            GUILayout.Label(ds.Length > 0 ? ds : "Writes the current voice language's moans, breaths, feeling sounds and dirty-talk lines to " + LipSync.DumpDir +
                            " as <clip>_<samples>.wav. Bake them with tools\\a2f\\bake.bat into " + System.IO.Path.Combine(LipSync.Dir, "game") + ".", ds.StartsWith("off") ? _off : _hint);
        }
    }
}
