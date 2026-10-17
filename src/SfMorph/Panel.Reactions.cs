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
            bool lines = _p.ReactLines.Value; float lineChanceCfg = _p.ReactLineChance.Value; bool linesGame = _p.ReactLinesGamePath.Value;
            bool faceClips = _p.ReactFaceClips.Value; float faceStrCfg = _p.ReactFaceStrength.Value;
            float sensCfg = _p.ReactSensitivity.Value, gapCfg = _p.ReactCooldown.Value, volCfg = _p.ReactVolume.Value;
            string sub = who ?? (reactOn ? null : "reactions are off");
            string guardOff = !c.Female || c.IsPlayer ? "only for the game's woman" : null;   // the guard needs neither the toggle nor touch tracking
            Reactions react = _body.React;
            string status = react.Status, stateLine = react.StateLine, bars = react.Bars, guardStatus = react.GuardStatus, lastLine = react.LastLine;
            float e = react.E, p = react.P;
            bool bridge = react.BridgeFound, diagBusy = react.DiagBusy, diagRan = react.DiagRan;

            GUI.enabled = who == null;
            bool on = GUILayout.Toggle(reactOn, " She reacts to touch: caress, grope, slaps (voice, face, flinch, slap sound, blush)");
            GUI.enabled = true;
            if (who == null && on != reactOn) _p.ReactOn.Value = on;
            if (who != null) { GUILayout.Label("    off: " + who, _off); GameVoiceBlock(guardOff, guard, guardStatus, react, bridge, diagBusy, diagRan); return; }
            if (reactOn && status.Length > 0) GUILayout.Label(status, status.StartsWith("off") ? _off : _hint);
            if (reactOn && lastLine.Length > 0) GUILayout.Label(lastLine, _hint);

            float sens = Slider("Sensitivity", sensCfg * 100f, 0f, 100f, "0", 50f, null, " %") / 100f;
            if (Mathf.Abs(sens - sensCfg) > 0.0001f) _p.ReactSensitivity.Value = sens;
            GUILayout.Label("0: only hard slaps count; 100: light taps too. Soft slap from 1.2 m/s, hard from 2.3, too hard from 4.5 (at 50 %).", _hint);
            float gap = Slider("Voice gap", gapCfg, 0.5f, 5f, "0.0", 1.5f, null, " s");
            if (Mathf.Abs(gap - gapCfg) > 0.0001f) _p.ReactCooldown.Value = gap;
            float vol = Slider("Slap volume", volCfg * 100f, 0f, 100f, "0", 60f, sound ? null : "off: Sound is off", " %") / 100f;
            if (sound && Mathf.Abs(vol - volCfg) > 0.0001f) _p.ReactVolume.Value = vol;
            // 0.9.0: skin foley
            bool foley = _p.ReactFoley.Value;
            GUI.enabled = sub == null && sound;
            bool tfo = GUILayout.Toggle(foley, " Skin sounds: slaps from your sound files, pat, grab, rub");
            GUI.enabled = true;
            if (sub == null && sound && tfo != foley) _p.ReactFoley.Value = tfo;
            // 0.9.4: the rub loop alone can be switched off (slaps, pats and grabs keep sounding)
            bool rubCfg = _p.ReactRubSound.Value;
            GUI.enabled = sub == null && sound && foley;
            bool trub = GUILayout.Toggle(rubCfg, "    Rub sound while a hand slides over her skin");
            GUI.enabled = true;
            if (sub == null && sound && foley && trub != rubCfg) _p.ReactRubSound.Value = trub;
            // 0.11.7: the pat / grab / rub have their own volume (they were scaled by the slap volume and inaudible at a quiet one)
            string skinOff = sub ?? (!sound ? "Sound is off" : !foley ? "skin sounds are off" : null);
            float skinCfg = _p.ReactSkinVolume.Value;
            float sv = Slider("    skin volume", skinCfg * 100f, 0f, 100f, "0", 80f, skinOff == null ? null : "off: " + skinOff, " %") / 100f;
            if (skinOff == null && Mathf.Abs(sv - skinCfg) > 0.0001f) _p.ReactSkinVolume.Value = sv;
            // 0.11.25: a finger going into her vagina, anus or mouth (your files in Sounds\penetration) and whether it excites her
            bool penCfg = _p.ReactPenSound.Value;
            GUI.enabled = skinOff == null;
            bool tpen = GUILayout.Toggle(penCfg, "    Penetration sound: a finger going into her, each in / out stroke");
            GUI.enabled = true;
            if (skinOff == null && tpen != penCfg) _p.ReactPenSound.Value = tpen;
            float pvCfg = _p.ReactPenVolume.Value;
            string penOff = skinOff ?? (!penCfg ? "the penetration sound is off" : null);
            float pv = Slider("    penetration volume", pvCfg * 100f, 0f, 100f, "0", 80f, penOff == null ? null : "off: " + penOff, " %") / 100f;
            if (penOff == null && Mathf.Abs(pv - pvCfg) > 0.0001f) _p.ReactPenVolume.Value = pv;
            bool prCfg = _p.ReactPenReact.Value;
            GUI.enabled = sub == null;
            bool tpr = GUILayout.Toggle(prCfg, " Penetration excites her (voice, lines, face; a finger in her mouth a little)");
            GUI.enabled = true;
            if (sub == null && tpr != prCfg) _p.ReactPenReact.Value = tpr;
            if (Penetration.Status.Length > 0) GUILayout.Label("    " + Penetration.Status.Replace("Penetration: ", ""), Penetration.Status.StartsWith("off") ? _off : _hint);
            if (!sound) GUILayout.Label("    off: Sound is off", _off);
            else if (foley)
            {
                var fs = ReactionsGame.Foley;
                GUILayout.Label(fs.Loaded ? fs.Summary.Replace("Skin foley: ", "") : fs.Failed ? "off: " + fs.Summary : "read on the first sound (WAV files in BepInEx\\plugins\\SfMorph\\Sounds)", fs.Failed ? _off : _hint);
            }

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
            GUILayout.Label("Face: the game's own eye reaction on slaps, with a wince or blink alongside when no moan carries the face. Gaze away: she looks away for a moment after a hard hit. " +
                            "Feed: touch also raises the game's own desire gauge.", _hint);

            FaceClipsBlock(react, sub, face, faceClips, faceStrCfg);
            LipSyncBlock(react, sub, face, faceClips);

            GUI.enabled = sub == null && voice;
            GUILayout.Label("Voice", _hint);
            int vm = GUILayout.Toolbar(voiceMode, VoiceModes);
            GUI.enabled = true;
            if (sub == null && voice && vm != voiceMode) _p.ReactVoiceMode.Value = vm;
            GUILayout.Label("Game picks: the game's own moan. By intensity: a moan chosen from her voice library by how hard the touch was.", _hint);

            // 0.8.6: her recorded lines for the touched area
            GUI.enabled = sub == null && voice;
            bool tli = GUILayout.Toggle(lines, " Speak lines for the touched area");
            GUI.enabled = true;
            if (sub == null && voice && tli != lines) _p.ReactLines.Value = tli;
            string linesOff = sub ?? (!voice ? "Voice is off" : !lines ? "lines are off" : null);
            float lch = Slider("Line chance", lineChanceCfg * 100f, 0f, 100f, "0", 50f, linesOff, " %") / 100f;
            if (linesOff == null && Mathf.Abs(lch - lineChanceCfg) > 0.0001f) _p.ReactLineChance.Value = lch;
            GUI.enabled = linesOff == null;
            bool tlg = GUILayout.Toggle(linesGame, " Through the game's dirty talk (her talking face and subtitle; off: the clip only)");
            GUI.enabled = true;
            if (linesOff == null && tlg != linesGame) _p.ReactLinesGamePath.Value = tlg;
            GUILayout.Label("The game's own recorded lines: breasts nipple lines, between her legs pussy (front) or anal (back) lines, butt, hips and thighs her general lines; " +
                            "level by excitement (tier 1-2 Lv1, 3 Lv2, 4 Lv3, none at tier 0). A feeling sound when her excitement rises a tier and at the climax. " +
                            "Sex or foreplay mode, the game's dirty talk on; never over dialogue, a kiss, the game's dirty talk or a moan; one line per 8 s at most.", _hint);

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

            GameVoiceBlock(guardOff, guard, guardStatus, react, bridge, diagBusy, diagRan);
            GUILayout.Label("Caress: a hand resting or stroking (about a quarter second, light). Grope: pressing hard (20 N or 1.5 cm deep). " +
                            "Slaps: by how fast the hand arrives (soft 1.2, hard 2.3, too hard 4.5 m/s at 50 % sensitivity). " +
                            "Excitement (tier 0 to 4) rises with touch and fades after ten quiet seconds; pain rises with hard slaps. " +
                            "Flinches need the 'Push her limbs' slider above 0 and soft zones or physics hands.", _hint);
        }

        // 0.8.7: her face clips (survey, mood picks, tests) and the game's own eye reactions / blink tests.
        void FaceClipsBlock(Reactions react, string sub, bool face, bool faceClips, float strCfg)
        {
            var fc = react.Face;
            GUILayout.Label("Face clips", _hint);
            GUI.enabled = sub == null && face;
            bool t = GUILayout.Toggle(faceClips, " Her face clips over the game's face (pleasure, ticklish, surprise, pain, climax)");
            GUI.enabled = true;
            if (sub == null && face && t != faceClips) _p.ReactFaceClips.Value = t;
            string off = sub ?? (!face ? "Face is off" : !faceClips ? "face clips are off" : null);
            float st = Slider("Face strength", strCfg * 100f, 0f, 100f, "0", 80f, off, " %") / 100f;
            if (off == null && Mathf.Abs(st - strCfg) > 0.0001f) _p.ReactFaceStrength.Value = st;
            if (off != null) GUILayout.Label("    off: " + off, _off);
            else if (fc.Status.Length > 0) GUILayout.Label(fc.Status, fc.Status.StartsWith("off") ? _off : _hint);
            if (off == null && fc.Surveyed)
            {
                for (int m = 0; m < ReactionsFace.NM; m++)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(ReactionsFace.MoodNames[m], GUILayout.Width(90));
                    GUI.enabled = fc.RankedCount > 0;
                    if (GUILayout.Button("<", GUILayout.Width(28))) _p.ReactFaceMap.Value = fc.Cycle(m, -1);
                    if (GUILayout.Button(">", GUILayout.Width(28))) _p.ReactFaceMap.Value = fc.Cycle(m, 1);
                    GUI.enabled = true;
                    string nm = fc.PickName(m);
                    GUILayout.Label(nm.Length > 0 ? nm + "  (" + fc.PickWhy(m) + ")" : "none", _hint, GUILayout.Width(330));
                    GUILayout.EndHorizontal();
                }
                GUI.enabled = !string.IsNullOrEmpty(_p.ReactFaceMap.Value);
                if (GUILayout.Button("Back to the automatic picks")) _p.ReactFaceMap.Value = "";
                GUI.enabled = true;
            }
            GUILayout.Label("A one-time survey samples every clip of her face Animator and ranks how much each moves her face (log and BepInEx\\config\\SfMorphs\\face-clips.txt). " +
                            "< > walk the ranked list for a mood (saved); the Debug tab plays one. Pleasure by excitement on breasts, hips, butt, thighs and neck; ticklish or surprised elsewhere; " +
                            "surprise on soft slaps, pain on hard ones. Her mouth stays the game's while she speaks or moans.", _hint);
        }

        // 0.9.9: lip sync from Audio2Face curves, and the mouth pose editor (one pose per ARKit shape, saved to arkit-map.txt)
        bool _lipOpen, _lipTongue, _lipPrev;
        int _lipShape, _lipBone;
        float _lipPrevW = 1f;
        string _lipMsg = "";

        void LipSyncBlock(Reactions react, string sub, bool face, bool faceClips)
        {
            var fc = react.Face;
            bool on = _p.LipSyncOn.Value;
            float strCfg = _p.LipStrength.Value, leadCfg = _p.LipLead.Value, overCfg = _p.LipOverGame.Value, moodCfg = _p.LipMood.Value;
            string off = sub ?? (!face ? "Face is off" : !faceClips ? "face clips are off (the lip sync uses their survey of her face)" : null);
            bool ready = off == null && fc.LipReady;
            bool open = _lipOpen, prev = _lipPrev;
            GUILayout.Label("Lip sync (Audio2Face curves)", _hint);
            GUI.enabled = off == null;
            bool t = GUILayout.Toggle(on, " Her mouth follows baked lip-sync curves for her voice clips");
            GUI.enabled = true;
            if (off == null && t != on) _p.LipSyncOn.Value = t;
            string soff = off ?? (!on ? "lip sync is off" : null);
            float st = Slider("Lip strength", strCfg * 100f, 0f, 150f, "0", 100f, soff, " %") / 100f;
            if (soff == null && Mathf.Abs(st - strCfg) > 0.0001f) _p.LipStrength.Value = st;
            float ld = Slider("Lip timing (+ earlier)", leadCfg * 1000f, -300f, 300f, "0", 0f, soff, " ms") / 1000f;
            if (soff == null && Mathf.Abs(ld - leadCfg) > 0.00001f) _p.LipLead.Value = ld;
            float ov = Slider("Replace the game's mouth", overCfg * 100f, 0f, 100f, "0", 50f, off, " %") / 100f;
            if (off == null && Mathf.Abs(ov - overCfg) > 0.0001f) _p.LipOverGame.Value = ov;
            // 0.11.11: her mood's mouth (smile, grimace) under the visemes; eyes and brows always stay the mood's
            float md = Slider("Keep her expression's mouth", moodCfg * 100f, 0f, 100f, "0", 70f, soff, " %") / 100f;
            if (soff == null && Mathf.Abs(md - moodCfg) > 0.0001f) _p.LipMood.Value = md;
            if (off != null) GUILayout.Label("    off: " + off, _off);
            else if (!fc.LipReady) GUILayout.Label("    waiting for her face survey (" + fc.Status + ")", _hint);
            else
            {
                string pk = ReactionsGame.Pack.Loaded ? ReactionsGame.Pack.Name : "";
                int nc = pk.Length > 0 ? LipSync.PackCurves(pk) : -1;
                GUILayout.Label((pk.Length > 0 ? "Pack '" + pk + "': " + (nc < 0 ? "no curves (" + System.IO.Path.Combine(LipSync.Dir, pk) + ")" : nc + " curves") : "No voice pack on")
                                + ". Map: " + LipSync.PosedShapes + " of " + LipSync.K + " shapes posed" + (LipSync.Dirty ? " (unsaved)" : "")
                                + (react.LipStatus.Length > 0 ? ". Last: " + react.LipStatus : ""), _hint);
            }
            if (GUILayout.Button("Rescan curves", GUILayout.Width(120))) { LipSync.Rescan(); _lipMsg = "curves are read again on next use"; }

            if (GUILayout.Button((open ? "- " : "+ ") + "Mouth poses for the lip sync", _bold)) { _lipOpen = !open; if (open) _lipPrev = false; }
            if (!open || !ready) react.LipPreview = -1;
            if (!open) return;
            if (!ready) { GUILayout.Label("    off: " + (off ?? "her face survey is not done yet"), _off); return; }
            LipSync.LoadMap(false);
            int K = LipSync.K;
            int shape = Mathf.Clamp(_lipShape, 0, K - 1);
            var names = new string[K];
            for (int i = 0; i < K; i++) names[i] = (LipSync.Map[i].Count > 0 ? "* " : "") + LipSync.Shapes[i];
            int ns = GUILayout.SelectionGrid(shape, names, 4);
            if (ns != shape) { _lipShape = ns; _lipBone = 0; }
            bool pv = GUILayout.Toggle(prev, " Preview this shape on her face");
            if (pv != prev) _lipPrev = pv;
            float pw = Slider("Preview weight", _lipPrevW * 100f, 0f, 100f, "0", 100f, prev ? null : "off: preview is off", " %") / 100f;
            if (prev) _lipPrevW = pw;
            react.LipPreview = prev ? shape : -1; react.LipPreviewW = _lipPrevW;

            // the bone being posed (jaw, lips, cheeks; tongue and teeth on request)
            var bones = new System.Collections.Generic.List<int>();
            for (int i = 0; i < fc.LipCount; i++) if (_lipTongue || !fc.LipIsTongue(i)) bones.Add(i);
            if (bones.Count == 0) { GUILayout.Label("    off: no mouth bones", _off); return; }
            int bi = Mathf.Clamp(_lipBone, 0, bones.Count - 1);
            int b = bones[bi];
            string bn = fc.LipName(b);
            var pose = LipSync.Find(shape, bn, false);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Bone", GUILayout.Width(170));
            if (GUILayout.Button("<", GUILayout.Width(32))) _lipBone = (bi - 1 + bones.Count) % bones.Count;
            GUILayout.Label(bn + (pose != null ? " *" : ""), GUILayout.Width(200));
            if (GUILayout.Button(">", GUILayout.Width(32))) _lipBone = (bi + 1) % bones.Count;
            bool tt = GUILayout.Toggle(_lipTongue, " tongue / teeth");
            GUILayout.EndHorizontal();
            if (tt != _lipTongue) { _lipTongue = tt; _lipBone = 0; }
            float mm = fc.LipMm(b);
            Vector3 P = pose != null ? pose.P * mm : Vector3.zero, R = pose != null ? pose.R : Vector3.zero;
            Vector3 nP = P, nR = R;
            nP.x = Slider("Move X", P.x, -10f, 10f, "+0.0;-0.0;0", 0f, null, " mm");
            nP.y = Slider("Move Y", P.y, -10f, 10f, "+0.0;-0.0;0", 0f, null, " mm");
            nP.z = Slider("Move Z", P.z, -10f, 10f, "+0.0;-0.0;0", 0f, null, " mm");
            nR.x = Slider("Turn X", R.x, -40f, 40f, "+0;-0;0", 0f, null, " deg");
            nR.y = Slider("Turn Y", R.y, -40f, 40f, "+0;-0;0", 0f, null, " deg");
            nR.z = Slider("Turn Z", R.z, -40f, 40f, "+0;-0;0", 0f, null, " deg");
            if ((nP - P).sqrMagnitude > 1e-8f || (nR - R).sqrMagnitude > 1e-6f)
            {
                pose = LipSync.Find(shape, bn, true);
                pose.P = nP / Mathf.Max(1e-6f, mm); pose.R = nR;
                LipSync.Changed();
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Build empty shapes")) { string r; fc.Seed(true, -1, out r); _lipMsg = r; }
            if (GUILayout.Button("Rebuild all")) { string r; fc.Seed(false, -1, out r); _lipMsg = r; }
            if (GUILayout.Button("Rebuild this shape")) { string r; fc.Seed(false, shape, out r); _lipMsg = r; }
            if (GUILayout.Button("Clear this shape")) { LipSync.Map[shape].Clear(); LipSync.Changed(); _lipMsg = LipSync.Shapes[shape] + " cleared"; }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(LipSync.Dirty ? "Save *" : "Save")) { LipSync.SaveMap(); _lipMsg = LipSync.MapStatus; }
            if (GUILayout.Button("Reload from file")) { LipSync.LoadMap(true); _lipMsg = LipSync.MapStatus; }
            GUILayout.EndHorizontal();
            if (_lipMsg.Length > 0) GUILayout.Label(_lipMsg, _hint);
            else if (LipSync.MapStatus.Length > 0) GUILayout.Label(LipSync.MapStatus, _hint);
            GUILayout.Label("One pose per ARKit shape at full weight: pick a shape (* = posed), turn on the preview, then move and turn each mouth bone until her mouth shows it. " +
                            "Build makes each shape from millimetre moves of her lips, corners and cheeks in her mouth's own frame (sized by her mouth width; only the jaw turns), as a start. " +
                            "The lip sync plays the curves through these poses (weighted sum, on top of the game's mouth by 'Replace the game's mouth'). Saved to " + LipSync.MapPath + ".", _hint);
        }

        // The game's dirty-talk voice error repair: independent of the reactions toggle, only needs her.
        void GameVoiceBlock(string whoOff, bool guard, string guardStatus, Reactions react, bool bridge, bool diagBusy, bool diagRan)
        {
            GUILayout.Label("Game voice", _bold);
            // 0.9.0: her voice's pitch and formant (also with Reactions off)
            float pc = _p.VoicePitch.Value, fc = _p.VoiceFormant.Value;
            float np = Slider("Voice pitch", pc, 0.75f, 1.4f, "0.00", 1f, whoOff, "");
            float nf = Slider("Voice formant", fc, 0.8f, 1.25f, "0.00", 1f, whoOff, "");
            if (whoOff == null && Mathf.Abs(np - pc) > 0.0001f) _p.VoicePitch.Value = np;
            if (whoOff == null && Mathf.Abs(nf - fc) > 0.0001f) _p.VoiceFormant.Value = nf;
            // 0.9.0: voice pack chooser (folders under Sounds\voice)
            string pk = _p.VoicePackName.Value ?? "";
            GUILayout.BeginHorizontal();
            GUILayout.Label("Voice pack", GUILayout.Width(170));
            GUI.enabled = whoOff == null;
            if (GUILayout.Button("<", GUILayout.Width(32))) _p.VoicePackName.Value = NextPack(pk, -1);
            GUILayout.Label(pk.Length > 0 ? pk : "the game's voice", GUILayout.Width(160));
            if (GUILayout.Button(">", GUILayout.Width(32))) _p.VoicePackName.Value = NextPack(pk, 1);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            var vp = ReactionsGame.Pack;
            // 0.11.7: the game's own moans and dirty talk silent while a pack is on
            bool mute = _p.VoiceMuteGame.Value;
            GUI.enabled = whoOff == null && pk.Length > 0;
            bool tm = GUILayout.Toggle(mute, " Mute the game's own voice while the pack is on (moans, sex voice, dirty talk; the pack moans in their place)");
            GUI.enabled = whoOff == null && pk.Length > 0 && mute;
            bool tdl = GUILayout.Toggle(_p.VoiceMuteDialogue.Value, "    ...and its dialogue voice (off: story dialogue stays audible)");
            GUI.enabled = true;
            if (whoOff == null && pk.Length > 0 && tm != mute) _p.VoiceMuteGame.Value = tm;
            if (whoOff == null && pk.Length > 0 && mute && tdl != _p.VoiceMuteDialogue.Value) _p.VoiceMuteDialogue.Value = tdl;
            if (pk.Length == 0) GUILayout.Label("    off: no voice pack chosen", _off);
            else if (mute) GUILayout.Label("    " + (react.GameVoiceMuted > 0 ? "muting " + react.GameVoiceMuted + " of the game's voice sources now" : "nothing of the game's playing now (needs Reactions on)") + (react.StandInNote.Length > 0 ? "; last " + react.StandInNote : ""), _hint);
            if (pk.Length > 0) GUILayout.Label(vp.Name == pk && vp.Summary.Length > 0 ? (vp.Loaded ? "" : "off: ") + vp.Summary : "loads within a second (in the game scene)", vp.Name == pk && !vp.Loaded && vp.Summary.Length > 0 ? _off : _hint);
            GUILayout.Label("A voice pack replaces her reaction moans, breaths, slap exertions and climax; a pack with lines also speaks for the touched area (with Lines on, no sex mode needed), guards, asks for more and talks in the afterglow. Her jaw follows the pack's audio (needs Face clips). The game's own lines and feeling sounds stay off while a pack is on.", _hint);
            string vfs = react.VoiceFxStatus;
            GUILayout.Label("Pitch: higher or lower without changing her speed (the lip sync stays). Formant: the size of her voice, below 1 fuller, above 1 smaller. 1 = as recorded."
                            + (vfs.Length > 0 ? " Now: " + vfs + "." : ""), _hint);
            GUI.enabled = whoOff == null;
            bool g = GUILayout.Toggle(guard, " Repair the game's voice shuffle errors (dirty talk and sex sound effects)");
            GUI.enabled = true;
            if (whoOff == null && g != guard) _p.VoiceGuard.Value = g;
            if (whoOff != null) { GUILayout.Label("    off: " + whoOff, _off); return; }
            if (guard && guardStatus.Length > 0) GUILayout.Label(guardStatus, guardStatus.StartsWith("off") ? _off : _hint);

        }

        // The next voice pack folder ("" = the game's voice comes first).
        static string NextPack(string cur, int dir)
        {
            var l = VoicePack.List();
            var all = new string[l.Length + 1];
            all[0] = ""; System.Array.Copy(l, 0, all, 1, l.Length);
            int i = System.Array.IndexOf(all, cur);
            if (i < 0) i = 0;
            return all[((i + dir) % all.Length + all.Length) % all.Length];
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
