using System.Text;
using UnityEngine;

namespace SfMorph
{
    // Her reactions to touch (0.7.9): what the Body hands the director each frame. Filled from the config once per frame (no allocation).
    internal class ReactionSettings
    {
        public bool PushByTouch, On, Voice, Face, Flinch, Sound, Blush, BlushBody, Gaze, FeedDesire, AutoState, AllowClimax, VoiceGuard, Lines, FaceClips, LinesGamePath, Foley, RubSound, MuteGameVoice, MuteDialogue, PackInScenes, PackNonEnglish, LipSync;
        public bool PenSound, PenReact; public float PenVolume;   // 0.11.25
        public float LipStrength, LipLead, LipOverGame, LipMood, LipBrows, LipSticky;
        public int LipFilter = 2; public bool LipAutoLead = true;   // 0.11.47
        public float Sensitivity, Cooldown, Volume, SkinVolume, BlushGain, VoicePitchVar, LimbPush, LineChance, FaceStrength, VoicePitch, VoiceFormant;
        public string FaceMap = "", VoicePack = "";
        public int VoiceMode, Log;

        public void Fill(Plugin p)
        {
            On = p.ReactOn.Value; Voice = p.ReactVoice.Value; Face = p.ReactFace.Value; Flinch = p.ReactFlinch.Value; Sound = p.ReactSound.Value;
            Blush = p.ReactBlush.Value; BlushBody = p.ReactBlushBody.Value; Gaze = p.ReactGaze.Value; FeedDesire = p.ReactFeedDesire.Value;
            AutoState = p.ReactAuto.Value; AllowClimax = p.ReactClimax.Value; VoiceGuard = p.VoiceGuard.Value;
            Sensitivity = p.ReactSensitivity.Value; Cooldown = p.ReactCooldown.Value; Volume = p.ReactVolume.Value; SkinVolume = p.ReactSkinVolume.Value; MuteGameVoice = p.VoiceMuteGame.Value; MuteDialogue = p.VoiceMuteDialogue.Value; PackInScenes = p.VoicePackInScenes.Value;
            BlushGain = p.ReactBlushGain.Value; VoicePitchVar = p.ReactVoicePitchVar.Value;
            VoiceMode = p.ReactVoiceMode.Value; Log = p.ReactLog.Value;
            Lines = p.ReactLines.Value; LineChance = p.ReactLineChance.Value;
            LinesGamePath = p.ReactLinesGamePath.Value;
            Foley = p.ReactFoley.Value; RubSound = p.ReactRubSound.Value; PenSound = p.ReactPenSound.Value; PenReact = p.ReactPenReact.Value; PenVolume = p.ReactPenVolume.Value; VoicePack = p.VoicePackName.Value ?? ""; PackNonEnglish = p.VoiceNonEnglish.Value; VoicePitch = p.VoicePitch.Value; VoiceFormant = p.VoiceFormant.Value;
            FaceClips = p.ReactFaceClips.Value; FaceStrength = p.ReactFaceStrength.Value; FaceMap = p.ReactFaceMap.Value ?? "";
            LipSync = p.LipSyncOn.Value; LipStrength = p.LipStrength.Value; LipLead = p.LipLead.Value; LipOverGame = p.LipOverGame.Value; LipMood = p.LipMood.Value; LipBrows = p.LipBrows.Value; LipSticky = p.LipSticky.Value;
            ReactionsFace.TrackRate = p.FaceTrackRate.Value; ReactionsFace.TrackBudget = p.FaceTrackBudget.Value;   // 0.11.47 quality knobs
            LipFilter = p.LipFilter.Value; LipAutoLead = p.LipAutoLead.Value;
            PushByTouch = p.HandsPushByTouch.Value;   // the geometric push of other hands: without it and without physics hands nothing is classified
            LimbPush = p.HandsLimbPush.Value;   // the flinch's swing is a push on her limbs: it needs the slider above 0
        }
    }

    internal enum RState { Idle, Aroused, Startled, Guarded, Climax, Afterglow }

    // The reactions director (0.7.9): classifies touches (caress, grope, slaps) per body segment, keeps her excitement (E) and pain (P),
    // derives her state, and drives the responses (voice, face, flinch, slap sound, blush, gaze) through ReactionsGame. Runs in
    // Body.ApplyAll for her, after the physics hands and before her soft zones simulate: it reads what HerBody / PhysicsHands wrote this
    // frame. Nothing here allocates, searches or calls GetComponent per frame; every game member lives in ReactionsGame.
    internal sealed class Reactions
    {
        public string Status = "", StateLine = "", Bars = "", GuardStatus = "", LastLine = "";
        public float E, P;
        public int Tier;
        public RState State;

        public static readonly string[] ZoneNames = { "Breast", "Chest", "Belly", "Hips", "Butt", "Thigh", "Calf", "Arm", "Face", "Neck" };

        // ---------------- tables (design 1.3, 2, 3) ----------------
        const int ZBreast = 0, ZChest = 1, ZBelly = 2, ZHips = 3, ZButt = 4, ZThigh = 5, ZCalf = 6, ZArm = 7, ZFace = 8, ZNeck = 9, NZ = 10;

        // excitement per event: caress, grope, soft slap, hard slap
        static readonly float[][] ZoneGain =
        {
            new[] { 0.012f, 0.03f, 0.03f, 0.04f },    // Breast
            new[] { 0.005f, 0.01f, 0.02f, 0.03f },    // Chest
            new[] { 0.004f, 0.01f, 0.02f, 0.03f },    // Belly
            new[] { 0.015f, 0.04f, 0.03f, 0.04f },    // Hips
            new[] { 0.008f, 0.025f, 0.035f, 0.05f },  // Butt
            new[] { 0.006f, 0.015f, 0.02f, 0.03f },   // Thigh
            new[] { 0.002f, 0.005f, 0.01f, 0.015f },  // Calf
            new[] { 0f, 0f, 0.005f, 0.01f },          // Arm
            new[] { 0.005f, 0f, 0f, 0f },             // Face
            new[] { 0.008f, 0.01f, 0.01f, 0.01f },    // Neck
        };
        static readonly float[] TierFactor = { 0f, 1f, 1f, 1f, 0.5f, 0f };                        // by event tier 0..5
        // 0.8.0: sized for Overlap.Kick's realistic inertias and critically damped settle (arm ~4 deg at the elbow, thigh ~1.5, head ~3, chest ~1 at I = 1)
        static readonly float[] KickNs = { 0.8f, 1.5f, 1.5f, 0f, 2.4f, 2.0f, 1.2f, 3.5f, 1.0f, 0.8f };
        static readonly bool[] SoftKick = { true, false, true, true, true, true, false, false, false, false };
        static readonly float[] ArrCool = { 0.4f, 0.4f, 0.4f, 0.4f, 0.3f, 0.4f, 0.4f, 0.4f, 0.4f, 0.4f };
        static readonly int[] HitFrom = { 0, 0, 0, 0, 1, 2, 2, 2, 3, 0 };                          // 0 front, 1 behind, 2 outside, 3 side
        static readonly float[] VoiceGap = { 2.5f, 2.0f, 1.5f, 1.0f, 0.7f };                      // by excitement tier
        static readonly float[] FlushAdd = { 0f, 0f, 0f, 0.15f, 0.3f, 0.4f };                      // by event tier
        static readonly string[] TierNames = { "brush", "caress", "grope", "soft slap", "hard slap", "too hard" };
        static readonly string[] StateNames = { "idle", "aroused", "startled", "guarded", "climax", "afterglow" };
        static readonly string[] ThighNames = { "thigh_L", "thigh_R" };
        static readonly float[] ClimaxAt = { 0f, 1.3f, 2.6f };
        static readonly float[] TestI = { 0f, 0.15f, 0.6f };                                      // caress, grope (slaps: from TestV, the same curve as the real ones)
        static readonly float[] TestV = { 0f, 0f, 0f, 1.8f, 3.4f, 5.5f };

        static int ZoneOfName(string n)
        {
            switch (n)
            {
                case "breast_L": case "breast_R": return ZBreast;
                case "chest": return ZChest;
                case "spine": return ZBelly;
                case "pelvis": return ZHips;
                case "butt_L": case "butt_R": return ZButt;
                case "thigh_L": case "thigh_R": return ZThigh;
                case "calf_L": case "calf_R": return ZCalf;
                case "upperarm_L": case "upperarm_R": case "forearm_L": case "forearm_R": return ZArm;
                case "head": return ZFace;
                case "neck": return ZNeck;
            }
            return -1;
        }
        static int SideOfName(string n) { return n.EndsWith("_L") ? 0 : n.EndsWith("_R") ? 1 : -1; }

        // ---------------- state ----------------
        struct SegR { public float ContactT, LastContactT, LastHitRead, PressT, LowT; public bool WasGeo, Pressed; }
        readonly SegR[] _r = new SegR[HerBody.SegNames.Length];
        readonly int[] _zoneOf = new int[HerBody.SegNames.Length], _sideOf = new int[HerBody.SegNames.Length];
        readonly float[] _zArr = new float[NZ], _zCaress = new float[NZ], _zGrope = new float[NZ], _zAny = new float[NZ];
        int _bv = -1;

        readonly ReactionsGame _g = new ReactionsGame();
        readonly ReactionsFace _face = new ReactionsFace();
        public ReactionsFace Face { get { return _face; } }
        float _lastFaceClipT = -100f;
        // zones whose caress / grope shows pleasure (the rest: ticklish or surprised): breast, chest, hips, butt, thigh, neck
        static readonly bool[] Erogenous = { true, true, false, true, true, true, false, false, false, true };
        readonly System.Random _rng = new System.Random();
        readonly StringBuilder _sb = new StringBuilder(160), _resp = new StringBuilder(160), _last = new StringBuilder(200);

        // set when the reactions part of Update threw (logged once); Body turns ReactOn off and clears it. The voice guard is not affected.
        public bool Failed;
        bool _failLogged;
        Character _her; Transform _root; ReactionSettings _s; Overlap _ov;
        float _now, _nextRefresh, _nextGate, _nextSummary;
        float _lastEvT = -100f, _lastStimT = -100f, _lastVoiceT = -100f, _lastBreathT = -100f, _lastFlinchT = -100f, _lastSlapT = -100f, _lastFaceT = -100f, _lastDesireT = -100f, _lastCaressFaceT = -100f;
        float _resetAt, _blink2At, _until, _guardHold, _climaxT0, _nextBreathAt, _lastStateLog = -100f, _startledAt = -100f;
        int _stChanges, _stSuppressed;
        // 0.8.6 lines: the last line / feeling sound, and the highest excitement tier already greeted with a feeling sound
        float _lastLineT = -100f, _lastEnterT = -100f;
        int _enterTier;
        bool _quiet;                       // set while a weaker hit of the same swing fires: no voice, sound or wince, only the rest
        string _moanKind = "moan";
        // the hits of one frame: a swing touches several segments at once and only the strongest is voiced / heard
        struct Hit { public int Zone, Tier; public HerBody.Seg G; public float I, V, Force; public Vector3 Pt, Dir; public string By; }
        readonly Hit[] _hits = new Hit[24];
        int _nHits;
        readonly System.Collections.Generic.Dictionary<string, int> _why = new System.Collections.Generic.Dictionary<string, int>();
        int _climaxIdx; bool _wasGuarded;
        // 0.9.0 foley: per segment where the touch point was in the segment's own frame and how fast it slides; the rub level of this frame
        struct Slide { public Vector3 L, Pt; public float Spd; public int Src; }
        readonly Slide[] _sl = new Slide[HerBody.SegNames.Length];
        float _rubLvl, _lastPatT = -100f, _lastGrabT = -100f;
        readonly bool[] _grabWas = new bool[2];
        // 0.11.25 penetration (Penetration.cs): per opening whether a finger is in, its depth, the stroke's extreme and direction (+1 in),
        // the depth speed (m/s, + inward), the last sound; _penK / _penEv are set while Fire runs for a penetration event
        struct PenR { public bool In, Seen; public float D, Ext, Spd, LastSnd; public int Dir, Strokes; }
        readonly PenR[] _pen = new PenR[Penetration.NO];
        int _penK = -1, _penEv;
        static readonly string[] PenEvNames = { "in", "in-stroke", "out", "out-stroke" };
        static readonly float[] PenEnterE = { 0.03f, 0.02f, 0.008f }, PenStrokeE = { 0.01f, 0.007f, 0.002f };   // vagina, anus, mouth
        Vector3 _rubPt, _testRubAt;
        float _testRubUntil = -1f;
        bool _rubNoted;
        // 0.9.0 face: the next pleasure face not before this (a random 2.5-5.5 s after each), and the voice-onset edge
        float _faceLock = -100f;
        bool _voiceWas;
        // 0.9.1: the pack's state lines (guard, afterglow) and the audio-driven jaw
        float _lastStateLineT = -100f, _afterLineAt, _jawOpen, _jawPeak = 0.05f;
        float _flush, _blushCur, _away, _gazeHold;
        bool _blushSent, _gazeSent, _wasOn, _bridgeLogged, _slapFail;
        int _evN, _barE = -1, _barP = -1, _stKey = int.MinValue, _slKey = int.MinValue;
        // 10 s summary
        readonly int[] _cnt = new int[6];
        int _vPlayed, _vSkipped, _sfx, _flinch;

        static bool Finite(Vector3 v) { return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)); }
        float Rand() { return (float)_rng.NextDouble(); }
        static void L(string m) { Plugin.Log.LogInfo("Reactions: " + m); }
        void Note(string s) { if (_resp.Length > 0) _resp.Append(", "); _resp.Append(s); }

        // ================= update =================

        // Runs in Body.ApplyAll for her, after the physics hands and before her soft zones simulate.
        public void Update(Character her, ReactionSettings s, float dt, Overlap overlap)
        {
            if (her == null || her.Root == null) { Release(); Status = "off: she is not in the scene"; return; }
            if (s == null) return;
            if (_root != null && _root != her.Root) { ResetSession(); try { _g.Release(); } catch { } _bridgeLogged = false; _nextRefresh = 0f; }
            _her = her; _root = her.Root; _s = s; _ov = overlap;
            if (!(dt > 0f)) return;                                  // paused
            bool cut = dt > 0.1f;                                    // a scene cut / load hitch: no events from this frame
            _now += Mathf.Min(dt, 0.1f);

            // the game link, the voice guard (independent of On) and the delayed trigger reset
            if (_now >= _nextRefresh) { _nextRefresh = _now + 1f; _g.Refresh(her, _now); _g.VoiceFx(s.VoicePitch, s.VoiceFormant); _g.UsePack(s.VoicePack, s.PackNonEnglish); }
            if (ReactionsGame.LipCurveFor == null) ReactionsGame.LipCurveFor = LipHasCurve;
            if (_g.Found && !_bridgeLogged) { _bridgeLogged = true; if (_g.BridgeLine != null && _g.BridgeLine.Length > 0) L(_g.BridgeLine); }
            else if (!_g.Found) _bridgeLogged = false;       // a re-found bridge is logged again
            _g.Pump(_now);
            _g.DiagTick(_now);                                       // the Diagnose button's state machine (idle unless it was pressed)
            _g.Guard(s.VoiceGuard, _now);
            GuardStatus = _g.GuardStatus != null ? _g.GuardStatus : "";
            if (_resetAt > 0f && _now >= _resetAt) { _resetAt = 0f; _g.ResetMoan(); }

            if (!s.On)
            {
                if (_wasOn) { _wasOn = false; GiveBack(); ResetSession(); }
                _g.Rub(Vector3.zero, 0f, 1f, dt);
                Status = "off: reactions are off"; StateLine = ""; Bars = ""; LastLine = ""; _stKey = int.MinValue; _slKey = int.MinValue; _barE = -1;
                return;
            }
            _wasOn = true;
            try
            {
                if (_now >= _nextGate) { _nextGate = _now + 0.1f; _g.ReadGates(_now); }
                // 0.8.7: the face clip survey and track building (a few samples per frame, restored in the same frame)
                _face.Tick(her, _g.Anim, s.Face && s.FaceClips && _g.Found, s.FaceMap, _now, dt);

                _rubLvl = 0f;
                if (HerBody.Built)
                {
                    if (HerBody.BuildVersion != _bv) RebuildMap();
                    if (cut) ClearR();
                    else { Classify(dt, s); GrabButtonSound(s); PenStep(dt, s); }
                }
                else if (_bv != -2) { _bv = -2; ClearR(); }
                // 0.9.0: the skin rub loop follows the fastest sliding touch of this frame
                // 0.9.4 (user: the rub sounds like carpet): its own switch, and a little quieter under the other skin sounds
                // 0.11.7: its own volume (Skin sounds), not the slap volume: at a quiet slap setting (0.12 in the user's config) the rub played
                // at 0.12 x 0.4 x the slide level, a few percent, and was never heard. The test button (TestResponse 7) holds it for 1.5 s.
                if (_now < _testRubUntil) { _rubLvl = 0.8f; _rubPt = _testRubAt; }
                bool rub = s.Sound && s.Foley && (s.RubSound || _now < _testRubUntil) && _rubLvl > 0f && State != RState.Climax;
                _g.Rub(_rubPt, rub ? _rubLvl * s.SkinVolume * 0.7f : 0f, 0.92f + 0.16f * _rubLvl, dt);
                if (rub && !_rubNoted) { _rubNoted = true; Note("rub sound"); if (s.Log >= 1) L("rub sound on (level " + _rubLvl.ToString("0.00") + ", volume " + (_rubLvl * s.SkinVolume * 0.7f).ToString("0.00") + ")"); }
                else if (!rub) _rubNoted = false;
                _g.MuteDialogue = s.MuteDialogue;
                // 0.11.35 (user: "pack replaces anyway"): a pack always stands in for her game voice wherever it has a fitting clip; the
                // game's own voice plays where it has none (MuteGameVoice is no longer a switch)
                _g.MuteGameVoice(ReactionsGame.Pack.Loaded);
                _g.SceneFill(s.PackInScenes && ReactionsGame.Pack.Loaded);

                if (cut) return;
                Tick(dt, s);
                Display(s);
            }
            catch (System.Exception e)
            {
                // only the reactions fail here: the caller turns off ReactOn; the voice guard above keeps running
                if (!_failLogged) { _failLogged = true; Plugin.Log.LogError("Reactions failed, turned off: " + e); }
                Failed = true;
                try { GiveBack(); ResetSession(); } catch { }
                Status = "off: reactions failed (see the log)"; StateLine = ""; Bars = "";
            }
        }

        // ---- the Diagnose button (see ReactionsGame.DiagStart) ----
        public bool BridgeFound { get { return _g.Found; } }
        public string VoiceFxStatus { get { return _g.VoiceFxStatus ?? ""; } }
        public int GameVoiceMuted { get { return _g.MutedNow; } }
        public string StandInNote { get { return _g.StandInNote; } }
        public bool DiagBusy { get { return _g.DiagBusy; } }
        public bool DiagRan { get { return _g.DiagRan; } }
        public void Diagnose()
        {
            if (!_g.Found) { L("diagnose: her game link is not found yet"); return; }
            if (_g.DiagBusy) return;
            _g.DiagStart(_now);
        }

        // Gives back everything we changed (blush, look-at weight, the guard's hooks) and forgets the session.
        public void Release()
        {
            try { if (_resetAt > 0f) _g.ResetMoan(); } catch { }     // the delayed "moan" trigger safety must not be lost
            try { _g.Release(); } catch { }
            try { _face.Release(); } catch { }
            ResetSession();
            _wasOn = false; _bridgeLogged = false; _nextRefresh = 0f; _resetAt = 0f;
            _her = null; _root = null; _s = null; _ov = null;
            _evN = 0; System.Array.Clear(_cnt, 0, _cnt.Length); _vPlayed = _vSkipped = _sfx = _flinch = 0; _nextSummary = 0f; _stChanges = _stSuppressed = 0; _why.Clear(); _nHits = 0;
            _slapFail = false; _bv = -1;
            Status = ""; StateLine = ""; Bars = ""; GuardStatus = ""; LastLine = "";
            _stKey = int.MinValue; _slKey = int.MinValue; _barE = -1; _barP = -1;
        }

        // The listeners' values we wrote go back (blush, look-at), once.
        void GiveBack()
        {
            if (_resetAt > 0f) { _resetAt = 0f; try { _g.ResetMoan(); } catch { } }
            if (_blushSent) { _blushSent = false; _g.SetBlush(0f, 0f); }
            if (_gazeSent) { _gazeSent = false; _g.SetGaze(0f); }
            try { _face.Stop(); } catch { }
            try { _g.RestorePitch(); } catch { }                       // the moan source gets its own pitch back
            try { _g.Rub(Vector3.zero, 0f, 1f, 1f); } catch { }
            try { _g.MuteGameVoice(false); } catch { }
            _blushCur = 0f; _away = 0f;
        }

        // Everything per-session about her mood and the classifier is forgotten (a cut, a new her, Reactions turned off).
        void ResetSession()
        {
            E = 0f; P = 0f; Tier = 0; State = RState.Idle; _wasGuarded = false;
            _flush = 0f; _gazeHold = 0f; _guardHold = 0f; _until = 0f; _climaxIdx = 3; _blink2At = 0f;
            _lastEvT = -100f; _lastStimT = -100f; _lastVoiceT = -100f; _lastBreathT = -100f; _lastFlinchT = -100f; _lastSlapT = -100f; _lastFaceT = -100f; _lastDesireT = -100f; _lastCaressFaceT = -100f;
            _startledAt = -100f; _nHits = 0; _quiet = false;
            _lastLineT = -100f; _lastEnterT = -100f; _enterTier = 0; _lastFaceClipT = -100f;
            _lastPatT = -100f; _lastGrabT = -100f; _faceLock = -100f; _voiceWas = false; _rubLvl = 0f;
            _lastStateLineT = -100f; _afterLineAt = 0f; _jawOpen = 0f;
            System.Array.Clear(_zArr, 0, NZ); System.Array.Clear(_zCaress, 0, NZ); System.Array.Clear(_zGrope, 0, NZ); System.Array.Clear(_zAny, 0, NZ);
            ClearR();
        }

        // Her segments changed: segment index -> zone and side, once per HerBody.BuildVersion.
        void RebuildMap()
        {
            _bv = HerBody.BuildVersion;
            for (int i = 0; i < _zoneOf.Length; i++) { _zoneOf[i] = -1; _sideOf[i] = -1; }
            var segs = HerBody.Segs;
            for (int i = 0; i < segs.Count; i++)
            {
                var g = segs[i];
                if (g.Index < 0 || g.Index >= _zoneOf.Length) continue;
                _zoneOf[g.Index] = ZoneOfName(g.Name); _sideOf[g.Index] = SideOfName(g.Name);
            }
            ClearR();
        }

        // The classifier forgets its contacts; hits that already happened are not arrivals.
        void ClearR()
        {
            System.Array.Clear(_r, 0, _r.Length);
            System.Array.Clear(_sl, 0, _sl.Length);
            System.Array.Clear(_pen, 0, _pen.Length);
            var segs = HerBody.Segs;
            for (int i = 0; i < segs.Count; i++)
                if (segs[i].Index >= 0 && segs[i].Index < _r.Length) _r[segs[i].Index].LastHitRead = segs[i].HitT;
        }

        // 0.11.22 (user: the grab sound doesn't work): it only played at the onset of a classified grope (20 N or 1.5 cm for 0.25 s), so
        // closing your hand on her - what a grab is - was silent. Now the grab button closing (Grip.Grab* crossing 0.6 from under 0.35) while
        // that physics hand touches her (contact within 0.3 s) plays it at the contact point; the grope onset keeps its own (0.5 s apart).
        void GrabButtonSound(ReactionSettings s)
        {
            if (!s.Sound || !s.Foley || _quiet) return;
            float fnow = Time.fixedTime;
            for (int h = 0; h < 2; h++)
            {
                float gv = h == 0 ? Grip.GrabLeft : Grip.GrabRight;
                bool closed = _grabWas[h] ? gv > 0.35f : gv >= 0.6f;
                bool edge = closed && !_grabWas[h];
                _grabWas[h] = closed;
                if (!edge || _now - _lastGrabT < 0.5f || PenByHand(h)) continue;   // 0.11.25: fingers in her: the penetration sound, not a grab
                HerBody.Seg best = null; float bt = 0.3f;
                var segs = HerBody.Segs;
                for (int i = 0; i < segs.Count; i++)
                {
                    float dtT = fnow - (h == 0 ? segs[i].TouchTL : segs[i].TouchTR);
                    if (dtT < bt) { bt = dtT; best = segs[i]; }
                }
                if (best == null) continue;
                _lastGrabT = _now;
                Vector3 pt = best.PhysPt.sqrMagnitude > 1e-8f ? best.PhysPt : SegCentre(best);
                bool ok = _g.FoleyShot(SkinFoley.Grab, pt, s.SkinVolume * Mathf.Lerp(0.6f, 0.9f, gv), Mathf.Lerp(0.94f, 1.06f, Rand()));
                if (ok) _sfx++;
                if (s.Log >= 1) L("grab sound: your " + (h == 0 ? "left" : "right") + " hand closed on " + best.Name + (ok ? "" : " (no sound: " + (_g.Found ? "sound effects off or no clip" : "no game link") + ")"));
            }
        }

        // ================= penetration (0.11.25) =================

        // A finger in an opening of this zone now (hips: vagina / anus; face: mouth).
        bool PenIn(int zone)
        {
            if (zone == ZHips) return _pen[Penetration.Vagina].In || _pen[Penetration.Anus].In;
            if (zone == ZFace) return _pen[Penetration.Mouth].In;
            return false;
        }
        bool PenByHand(int h)
        {
            for (int k = 0; k < Penetration.NO; k++) if (_pen[k].In && Penetration.Hand[k] == h) return true;
            return false;
        }

        // The depth of the deepest finger in each opening (Penetration, filled this frame by HerBody) -> events: in (past the opening by
        // 6 mm), out (4 mm back out, or gone), and while in, a stroke each time the depth turns by 12 mm (in-stroke / out-stroke).
        void PenStep(float dt, ReactionSettings s)
        {
            bool fresh = Penetration.Frame == Time.frameCount;
            for (int k = 0; k < Penetration.NO; k++)
            {
                float d = fresh && Penetration.Ok[k] ? Penetration.Depth[k] : float.NaN;
                if (float.IsNaN(d))
                {
                    if (_pen[k].In) { _pen[k].In = false; PenEvent(k, 2, Mathf.Max(0f, -_pen[k].Spd), Penetration.O[k], Penetration.By[k] ?? "a hand", s); }
                    _pen[k].Seen = false; _pen[k].Spd = 0f;
                    continue;
                }
                if (_pen[k].Seen)
                {
                    float v = (d - _pen[k].D) / Mathf.Max(dt, 1e-4f);
                    if (Mathf.Abs(v) < 3f) _pen[k].Spd += (v - _pen[k].Spd) * Mathf.Clamp01(dt / 0.05f);   // a jump is not a stroke
                }
                else _pen[k].Spd = 0f;
                _pen[k].D = d; _pen[k].Seen = true;
                string by = Penetration.By[k] ?? "a hand";
                Vector3 at = Penetration.O[k];
                if (!_pen[k].In)
                {
                    if (d > 0.006f) { _pen[k].In = true; _pen[k].Dir = 1; _pen[k].Ext = d; _pen[k].Strokes = 0; PenEvent(k, 0, Mathf.Max(0f, _pen[k].Spd), at, by, s); }
                    continue;
                }
                if (d < -0.004f) { _pen[k].In = false; PenEvent(k, 2, Mathf.Max(0f, -_pen[k].Spd), at, by, s); continue; }
                if (_pen[k].Dir > 0)
                {
                    if (d > _pen[k].Ext) _pen[k].Ext = d;
                    else if (d < _pen[k].Ext - 0.012f) { _pen[k].Dir = -1; _pen[k].Ext = d; _pen[k].Strokes++; PenEvent(k, 3, Mathf.Max(0f, -_pen[k].Spd), at, by, s); }
                }
                else
                {
                    if (d < _pen[k].Ext) _pen[k].Ext = d;
                    else if (d > _pen[k].Ext + 0.012f) { _pen[k].Dir = 1; _pen[k].Ext = d; _pen[k].Strokes++; PenEvent(k, 1, Mathf.Max(0f, _pen[k].Spd), at, by, s); }
                }
            }
        }

        // One penetration event (ev: 0 in, 1 in-stroke, 2 out, 3 out-stroke) at speed spd (m/s along the opening): the sound (at most 5 a
        // second per opening, louder with speed, the way out a little softer), and for in / in-stroke her reaction like a grope (Fire with
        // _penK set: its own excitement gains, the area's lines, no grab sound). Logged at Log 1 (strokes: the first 20 of an entry), Log 2 all.
        void PenEvent(int k, int ev, float spd, Vector3 at, string by, ReactionSettings s)
        {
            float I = Mathf.Clamp01((spd - 0.03f) / 0.3f);
            string snd;
            if (!(s.Sound && s.Foley && s.PenSound)) snd = "sound off";
            else if (_now - _pen[k].LastSnd < 0.2f) snd = "sound skipped (too soon)";
            else
            {
                float vol = s.SkinVolume * s.PenVolume * Mathf.Lerp(0.35f, 1f, I) * (ev >= 2 ? 0.7f : 1f) * (k == Penetration.Mouth ? 0.6f : 1f);
                bool ok = _g.FoleyShot(SkinFoley.Penetration, at, vol, Mathf.Lerp(0.94f, 1.06f, Rand()) + (ev >= 2 ? 0.03f : 0f));
                if (ok) { _pen[k].LastSnd = _now; _sfx++; snd = "sound " + vol.ToString("0.00"); }
                else snd = "no sound (" + (!_g.Found ? "no game link" : !_g.FoleyReady() ? "skin sounds not loaded"
                         : !ReactionsGame.Foley.HasFiles(SkinFoley.Penetration) ? "no files in Sounds, penetration folder" : "sound effects off in the game") + ")";
            }
            string re = "";
            if ((ev == 0 || ev == 1) && s.PenReact)
            {
                var g = HerBody.Find(k == Penetration.Mouth ? "head" : "pelvis");
                if (g != null && g.Index >= 0)
                {
                    float e0 = E;
                    _penK = k; _penEv = ev;
                    try { Fire(k == Penetration.Mouth ? ZFace : ZHips, g, 2, Mathf.Max(I, 0.1f), at, Penetration.N[k], by, spd, 0f, false); }
                    finally { _penK = -1; }
                    re = "; E " + e0.ToString("0.00") + " -> " + E.ToString("0.00") + (_resp.Length > 0 ? " (" + _resp + ")" : "");
                }
                else re = "; no " + (k == Penetration.Mouth ? "head" : "pelvis") + " segment: no reaction";
            }
            bool logIt = s.Log >= 2 || (s.Log == 1 && (ev == 0 || ev == 2 || _pen[k].Strokes <= 20));
            if (logIt) L("penetration: " + Penetration.Names[k] + " " + PenEvNames[ev] + " by " + by + " (" + spd.ToString("0.00") + " m/s, depth " + (_pen[k].D * 100f).ToString("0.0") + " cm) -> " + snd + re);
        }

        // ================= classifier (design 1.4) =================

        void Classify(float dt, ReactionSettings s)
        {
            float k = Mathf.Lerp(1.4f, 0.6f, Mathf.Clamp01(s.Sensitivity));    // 1 at 50 %: soft slap 1.2 m/s, hard 2.3, too hard 4.5
            float fnow = Time.fixedTime, win = 2.5f * Time.fixedDeltaTime + 0.01f;
            var segs = HerBody.Segs;
            for (int i = 0; i < segs.Count; i++)
            {
                var g = segs[i];
                int gi = g.Index;
                if (gi < 0 || gi >= _r.Length) continue;
                int zone = _zoneOf[gi];
                if (zone < 0) continue;
                bool geo = g.GeoDepth > 0f;
                bool touching = fnow - g.TouchT <= win || geo;
                float force = Mathf.Max(g.PhysF.magnitude, g.GeoF.magnitude);

                // arrival of a hand: the physics hands' contact enter, or the geometric onset of any other hand
                bool arrived = false; float v = 0f; Vector3 pt = Vector3.zero, dir = Vector3.zero; string by = null;
                if (g.HitT > _r[gi].LastHitRead)
                {
                    _r[gi].LastHitRead = g.HitT;
                    if (fnow - g.HitT < 0.3f)
                    {
                        arrived = true; v = Mathf.Max(g.HitVn, 0.6f * g.HitSpeed); pt = g.HitPt; dir = g.HitDir;
                        by = g.HitLeft ? "your left physics hand" : "your right physics hand";
                    }
                }
                else if (geo && !_r[gi].WasGeo && _now - _r[gi].LastContactT > 0.3f)
                {
                    arrived = true; v = g.GeoVn; pt = g.GeoPt;
                    dir = g.GeoF.sqrMagnitude > 1e-8f ? g.GeoF.normalized : (SegCentre(g) - pt);
                    by = g.GeoBy != null && g.GeoBy.Name != null ? g.GeoBy.Name : (g.GeoName != null ? g.GeoName : "a hand");
                }
                _r[gi].WasGeo = geo;

                if (arrived && v < 8f)                                  // >= 8 m/s: the rig jumped, not a hand
                {
                    int t = v >= 4.5f * k ? 5 : v >= 2.3f * k ? 4 : v >= 1.2f * k ? 3 : 0;
                    if (t > 0 && _now - _zArr[zone] >= ArrCool[zone])
                        Collect(zone, g, t, Mathf.Clamp01((v - 1.2f * k) / (3.3f * k)), pt, dir, by, v, force);   // one continuous strength over all slap tiers
                    // 0.9.0: a light landing below the soft slap is a pat (only a sound: it moves nothing else)
                    else if (t == 0 && v >= 0.45f * k && s.Sound && s.Foley && _now - _lastPatT >= 0.12f && _now - _zArr[zone] >= ArrCool[zone])
                    {
                        _lastPatT = _now;
                        float pv = Mathf.Clamp01((v - 0.45f * k) / (0.75f * k));
                        bool ok = _g.FoleyShot(SkinFoley.Pat, Finite(pt) ? pt : SegCentre(g), s.SkinVolume * Mathf.Lerp(0.4f, 0.9f, pv), Mathf.Lerp(0.95f, 1.08f, Rand()));
                        if (ok) _sfx++;
                        if (s.Log >= 2) L("pat on " + g.Name + " by " + (by ?? "a hand") + " (" + v.ToString("0.00") + " m/s)" + (ok ? "" : ": no sound"));
                    }
                }

                // 0.9.0 rub: how fast the touch point slides over her skin, in the segment's own frame (her animation doesn't count)
                TrackSlide(gi, g, touching, geo, force, dt);

                // contact time
                if (touching) { _r[gi].ContactT += dt; _r[gi].LastContactT = _now; _lastStimT = _now; }
                else if (_now - _r[gi].LastContactT > 0.3f) _r[gi].ContactT = 0f;

                // a brush or a resting hand only drips (a fifth of the caress gain per second)
                if (touching && s.On)
                {
                    float sf = StateFactor();
                    if (sf > 0f) E = Mathf.Clamp01(E + ZoneGain[zone][0] * 0.2f * dt * sf);
                }

                // grope: pressing hard (or deep) for 0.25 s, once, re-armed after 0.5 s of a light touch
                bool pressing = force >= 20f * k || g.GeoDepth >= 0.015f * k || g.YieldPen >= 0.015f * k;   // 0.8.0: a yielded limb drops the contact force
                _r[gi].PressT = pressing ? _r[gi].PressT + dt : 0f;
                bool low = force < 10f && g.GeoDepth < 0.005f && g.YieldPen < 0.005f;
                _r[gi].LowT = low ? _r[gi].LowT + dt : 0f;
                if (_r[gi].LowT >= 0.5f) _r[gi].Pressed = false;
                bool fired = false;
                if (pressing && _r[gi].PressT >= 0.25f && !_r[gi].Pressed && _now - _zGrope[zone] >= 2f && _now - _zArr[zone] >= 0.5f && !Pending(zone))
                {
                    _r[gi].Pressed = true; _zGrope[zone] = _now; fired = true;
                    if (ZoneGain[zone][1] > 0f)
                    {
                        float I = Mathf.Max(Mathf.Max(Mathf.Clamp01((force - 20f * k) / 40f), Mathf.Clamp01(g.GeoDepth / 0.04f)), Mathf.Clamp01(g.YieldPen / 0.04f));
                        Fire(zone, g, 2, Mathf.Max(I, 0.1f), g.PhysF.sqrMagnitude >= g.GeoF.sqrMagnitude && g.PhysF.sqrMagnitude > 0f ? g.PhysPt : g.GeoPt,
                            g.PhysF.sqrMagnitude >= g.GeoF.sqrMagnitude ? g.PhysF : g.GeoF, ByOf(g), 0f, force, false);
                    }
                }
                // caress: a light touch held 0.25 s, again every 1.5 s while held
                if (!fired && touching && !pressing && _r[gi].ContactT >= 0.25f && force < 12f * k && _now - _zCaress[zone] >= 1.5f && _now - _zAny[zone] >= 0.8f)
                {
                    _zCaress[zone] = _now;
                    if (ZoneGain[zone][0] > 0f)
                    {
                        Vector3 cp = g.PhysF.sqrMagnitude >= g.GeoF.sqrMagnitude && g.PhysF.sqrMagnitude > 0f ? g.PhysPt : (g.GeoW > 0f ? g.GeoPt : SegCentre(g));
                        Fire(zone, g, 1, 0.15f, cp, SegCentre(g) - cp, ByOf(g), 0f, force, false);
                    }
                }
            }
            FireHits();
        }

        void TrackSlide(int gi, HerBody.Seg g, bool touching, bool geo, float force, float dt)
        {
            int src = !touching || g.A == null ? 0 : g.PhysF.sqrMagnitude > 1e-6f ? 1 : geo ? 2 : 0;
            if (src == 0) { _sl[gi].Src = 0; _sl[gi].Spd = 0f; return; }
            Vector3 cp = src == 1 ? g.PhysPt : g.GeoPt;
            if (!Finite(cp)) { _sl[gi].Src = 0; return; }
            Vector3 lp = g.A.InverseTransformPoint(cp);
            if (_sl[gi].Src == src)
            {
                float sp = g.A.TransformVector(lp - _sl[gi].L).magnitude / Mathf.Max(dt, 1e-4f);
                if (sp < 3f) _sl[gi].Spd += (sp - _sl[gi].Spd) * Mathf.Clamp01(dt / 0.08f);   // a jump of the contact point is not a slide
            }
            else _sl[gi].Spd = 0f;
            _sl[gi].L = lp; _sl[gi].Pt = cp; _sl[gi].Src = src;
            // from 4 cm/s, full at 40 cm/s; a firmer hand is a little louder
            float lvl = Mathf.Clamp01((_sl[gi].Spd - 0.04f) / 0.36f) * Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(force / 20f));
            if (PenIn(_zoneOf[gi])) lvl = 0f;                        // 0.11.25: a finger moving in her is not a rub on her skin
            if (lvl > _rubLvl) { _rubLvl = lvl; _rubPt = cp; }
        }

        // A slap arrival of this frame: held until the whole frame was seen (the swing touches several segments at once).
        // One hit per zone: two segments of one zone in the same frame are compared and the faster one stays (the zone's arrival cooldown is
        // written in FireHits, for the hit that fires).
        void Collect(int zone, HerBody.Seg g, int tier, float I, Vector3 pt, Vector3 dir, string by, float v, float force)
        {
            var h = new Hit { Zone = zone, G = g, Tier = tier, I = I, V = v, Force = force, Pt = pt, Dir = dir, By = by };
            for (int i = 0; i < _nHits; i++)
                if (_hits[i].Zone == zone) { if (v > _hits[i].V) _hits[i] = h; return; }
            if (_nHits >= _hits.Length) return;
            _hits[_nHits++] = h;
        }

        bool Pending(int zone)
        {
            for (int i = 0; i < _nHits; i++) if (_hits[i].Zone == zone) return true;
            return false;
        }

        // The strongest hit of the frame is voiced, heard and winced; the others of the same swing only move her (excitement, pain, flinch, blush).
        void FireHits()
        {
            if (_nHits == 0) return;
            int best = 0;
            for (int i = 1; i < _nHits; i++) if (_hits[i].V > _hits[best].V) best = i;
            for (int i = 0; i < _nHits; i++) _zArr[_hits[i].Zone] = _now;
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < _nHits; i++)
                {
                    if ((i == best) != (pass == 0)) continue;
                    _quiet = i != best;
                    var h = _hits[i];
                    Fire(h.Zone, h.G, h.Tier, h.I, h.Pt, h.Dir, h.By, h.V, h.Force, false);
                }
            _quiet = false; _nHits = 0;
        }

        // who is touching the segment now (for the log): the physics hands' side, or the geometric hand
        static string ByOf(HerBody.Seg g)
        {
            if (g.PhysF.sqrMagnitude >= g.GeoF.sqrMagnitude && g.PhysF.sqrMagnitude > 0f) return g.PushRight ? "your right physics hand" : "your left physics hand";
            if (g.GeoBy != null && g.GeoBy.Name != null) return g.GeoBy.Name;
            return g.GeoName != null ? g.GeoName : "a hand";
        }

        // Where a segment is now: sphere centre, the middle of A-B, or the head's shape centre.
        Vector3 SegCentre(HerBody.Seg g)
        {
            if (g.A == null) return _root != null ? _root.position : Vector3.zero;
            if (g.Sphere) return g.A.TransformPoint(g.LocalCenter);
            if (g.Space == 1)
            {
                Vector3 p, sc; Quaternion q;
                if (HerBody.SegPose(g, out p, out q, out sc)) return p + q * (g.FrameCenter * sc.x);
                return g.A.position;
            }
            return g.B != null ? (g.A.position + g.B.position) * 0.5f : g.A.position;
        }

        // ================= events (design 2, 3) =================

        float StateFactor()
        {
            switch (State)
            {
                case RState.Guarded: case RState.Afterglow: case RState.Startled: return 0.5f;   // slaps still raise her a little while she is startled
                case RState.Climax: return 0f;
            }
            return 1f;
        }

        void SetTier() { Tier = Mathf.Clamp((int)(E / 0.2f), 0, 4); }

        // One classified touch: tier 1 caress, 2 grope, 3 soft slap, 4 hard slap, 5 too hard; I = strength 0..1.
        void Fire(int zone, HerBody.Seg g, int tier, float I, Vector3 pt, Vector3 dir, string by, float v, float force, bool test)
        {
            var s = _s;
            I = Mathf.Clamp01(I);
            if (!Finite(dir) || !(dir.sqrMagnitude > 1e-6f)) dir = -_root.forward; else dir.Normalize();
            if (!Finite(pt)) pt = SegCentre(g);
            bool wasGuarded = State == RState.Guarded;
            float e0 = E;
            int t0 = Tier;
            _resp.Length = 0;
            _zAny[zone] = _now; if (tier >= 3) _zArr[zone] = _now;

            // excitement and pain (0.11.25: a penetration event has its own gains; a fast entry into her anus hurts a little)
            float gain = _penK >= 0 ? (_penEv == 0 ? PenEnterE[_penK] : PenStrokeE[_penK] * Mathf.Lerp(0.5f, 1f, I)) : ZoneGain[zone][Mathf.Min(tier, 4) - 1] * TierFactor[tier];
            E = Mathf.Clamp01(E + gain * StateFactor());
            float dP = _penK >= 0 ? (_penK == Penetration.Anus && _penEv == 0 && I > 0.6f ? 0.1f * I : 0f)
                     : tier == 4 ? 0.15f * I * (zone == ZFace ? 2f : 1f) : tier == 5 ? 0.35f : (tier == 2 && zone == ZNeck ? 0.1f : 0f);
            P = Mathf.Clamp01(P + dP);
            _lastEvT = _now; _lastStimT = _now;
            SetTier();
            bool rose = Tier > t0 && Tier > _enterTier;   // a tier she has not reached since it last fell: greeted with a feeling sound
            if (rose) _enterTier = Tier;
            _cnt[tier]++;

            // states that start with an event
            if (s.AutoState && tier >= 4 && State != RState.Climax && _now - _startledAt >= 3f)   // not again within 3 s: no idle/startled flapping
            {
                _wasGuarded = wasGuarded || (State == RState.Startled && _wasGuarded);
                _startledAt = _now;
                EnterState(RState.Startled, 1.5f);
                if (s.Face) _blink2At = _now + 0.4f;
            }

            // voice (one slot per swing: _quiet hits and weak taps move her but are not voiced)
            bool voiced = false;
            if (s.Voice)
            {
                // 0.8.6: a feeling sound when her excitement rose a tier; else, sometimes, one of her lines for the touched area instead of a moan
                bool calm = State != RState.Startled && State != RState.Guarded && !wasGuarded;
                if (!_quiet && s.Lines && rose && calm && TryFeeling(Tier, false)) voiced = true;
                else if (!_quiet && s.Lines && tier <= 3 && calm && TryLine(zone, g, pt, false)) voiced = true;
                if (voiced) _vPlayed++;
                float chance = tier == 1 ? 0.15f + 0.1f * Tier : tier == 2 ? 0.5f + 0.1f * Tier : tier == 3 ? 0.6f + 0.1f * Tier : 1f;
                if (tier <= 2 && (wasGuarded || State == RState.Startled)) chance = 0f;
                // 0.9.1: touched while she is guarded: sometimes a guard line from the pack ("not yet", "give me some space")
                if (!voiced && !_quiet && tier <= 2 && wasGuarded && s.Lines && Rand() < 0.35f && TryStateLine(VoicePack.SGuard, "touched while guarded")) { voiced = true; Note("guard line"); }
                if (test) chance = 1f;
                if (voiced) { }                    // a line or feeling sound carried the voice
                else if (_quiet) { if (tier >= 3) { Skip("same swing"); Note("voice skipped (same swing)"); } }
                else if (tier >= 3 && I < 0.12f && !test) { Skip("weak tap"); Note("voice skipped (weak tap)"); }
                else if (!(chance >= 1f || Rand() < chance)) Note("no voice (chance " + chance.ToString("0.00") + ")");
                else
                {
                    if (_g.Found) _g.ReadGatesNow(_now);   // forced: ReadGates itself only reads every 0.1 s
                    bool cut = tier >= 4 && I >= 0.5f;
                    string block = VoiceBlock(tier, tier == 1, true, I);
                    if (block != null) { Skip(block); Note("voice skipped (" + block + ")"); }
                    else
                    {
                        bool ok;
                        if (tier == 1) { ok = ReactionsGame.Pack.Loaded ? _g.PackBreath(Tier >= 2) : _g.Breath(); if (ok) _lastBreathT = _now; _moanKind = ReactionsGame.Pack.Loaded ? "breath (pack)" : "breath"; }
                        else ok = PlayMoan(cut, tier);
                        if (ok) { voiced = true; _vPlayed++; Note(_moanKind); }
                        else { Skip("voice failed"); Note("voice failed"); }
                    }
                }
            }
            if (tier >= 3 && s.Face && !_quiet) DoFace(tier, I, voiced);
            else if (s.Face && !_quiet)
            {
                // a caress at excitement tier >= 1 sometimes shows in her eyes (own 4 s gap); a grope more often
                if (tier == 1 && Tier >= 1 && _now - _lastCaressFaceT >= 4f && Rand() < 0.25f + 0.1f * Tier) { if (FaceSoft(1)) _lastCaressFaceT = _now; }
                else if (tier == 2 && Rand() < 0.5f) FaceSoft(2);
            }
            if (s.Face && s.FaceClips && !_quiet) FaceClip(zone, tier, I);

            // flinch, slap sound, blush, gaze, the game's desire
            if (tier >= 3)
            {
                float Ie = Mathf.Max(I, 0.1f);   // a weak tap is not voiced (rule above on the raw I) but still moves her and reddens a little
                if (s.Flinch) DoFlinch(zone, g, tier, Ie, pt, dir, wasGuarded, false);
                if (s.Sound && !_quiet) DoSlap(tier, I, pt, false);
                if (s.Blush)
                {
                    float add = FlushAdd[tier] * Ie * (zone == ZFace ? 2f : 1f);
                    _flush = Mathf.Min(0.65f, _flush + add);
                    Note("blush +" + (add * s.BlushGain).ToString("0.00"));
                }
                // 0.8.0: slap mark (redness) and ripple at the hit; both are no-ops while their feature is off
                SkinMarks.Slap(pt, 0.07f, I);
                Ripple.Impact(pt, dir, Mathf.Max(v, 1f), g != null ? g.A : null, by);
                if (s.Gaze && (tier >= 4 || wasGuarded)) _gazeHold = Mathf.Max(_gazeHold, _now + (tier >= 5 ? 1.5f : 0.4f));
            }
            // 0.9.0: a grab sound when a hand closes on her (the grope's onset)
            if (tier == 2 && s.Sound && s.Foley && !_quiet && _now - _lastGrabT >= 0.5f && _penK < 0 && !PenIn(zone))
            {
                _lastGrabT = _now;
                if (_g.FoleyShot(SkinFoley.Grab, pt, s.SkinVolume * Mathf.Lerp(0.45f, 0.9f, I), Mathf.Lerp(0.92f, 1.08f, Rand()))) { _sfx++; Note("grab sound"); }
            }
            // the game's desire: groping and slaps (every 3 s); a caress only once she is well aroused (excitement tier 3) and every 6 s
            if (s.FeedDesire && (tier >= 2 || (tier == 1 && Tier >= 3)) && _g.InSexMode && _now - _lastDesireT >= (tier == 1 ? 6f : 3f)) { _lastDesireT = _now; _g.FeedDesire(); Note("desire"); }

            if (s.AutoState && s.AllowClimax && E >= 1f && State != RState.Climax && State != RState.Afterglow) EnterClimax();

            // log
            // (only the voiced best hit of a swing is the event: the quiet ones of the same swing show up in the log at Log 2 only)
            if (!_quiet) _evN++;
            bool logIt = _penK >= 0 ? false                         // PenEvent logs it with the sound
                       : _quiet ? s.Log >= 2 : s.Log >= 2 || (s.Log == 1 && (test || _evN <= 20 || _evN % 10 == 0));
            if (logIt)
            {
                _sb.Length = 0;
                _sb.Append(TierNames[tier]).Append(" on ").Append(g.Name).Append(" by ").Append(by != null ? by : "a hand");
                if (test) _sb.Append(" (test)");
                _sb.Append(" (");
                if (tier >= 3) _sb.Append(v.ToString("0.0")).Append(" m/s"); else _sb.Append(force.ToString("0")).Append(" N");
                _sb.Append(", I ").Append(I.ToString("0.00")).Append(')');
                if (_resp.Length > 0) _sb.Append(" -> ").Append(_resp.ToString());
                _sb.Append("; E ").Append(e0.ToString("0.00")).Append(" -> ").Append(E.ToString("0.00")).Append(" (tier ").Append(Tier)
                   .Append("), P ").Append(P.ToString("0.00")).Append("; state ").Append(StateNames[(int)State]);
                L(_sb.ToString());
            }

            // the panel's "last:" line (built per event only, for the best hit of a swing)
            if (_quiet) return;
            _last.Length = 0;
            if (_penK >= 0) _last.Append("last: penetration (").Append(Penetration.Names[_penK]).Append(' ').Append(PenEvNames[_penEv]).Append(") by ").Append(by != null ? by : "a hand");
            else _last.Append("last: ").Append(TierNames[tier]).Append(" on ").Append(ZoneNames[zone]).Append(" by ").Append(by != null ? by : "a hand");
            if (test) _last.Append(" (test)");
            _last.Append(" -> ").Append(_resp.Length > 0 ? _resp.ToString() : "no visible response");
            _last.Append("  [E ").Append(E.ToString("0.00")).Append(", ").Append(_g.InSexMode ? "sex mode" : "outside sex mode").Append(']');
            LastLine = _last.ToString();
        }

        void Skip(string why)
        {
            _vSkipped++;
            int c; _why.TryGetValue(why, out c); _why[why] = c + 1;
        }

        // The arbiter (design 3.1): null = the voice may play, else why not. gap: also apply the tier gap.
        // Real scripted speech (dialogue, a kiss, the dialogue source) blocks everything. The game's feeling loops (sexFacialPlaying) and its
        // dirty-talk pause only stop caresses and gropes: a slap (tier 3+) still goes through the game's own touch path. A hard slap (tier 4+
        // with strength >= 0.5) cuts a moan that is still sounding.
        string VoiceBlock(int tier, bool breath, bool gap, float I, bool facePauseOk = false)
        {
            if (!_g.Found) return "no game link";
            bool pack = ReactionsGame.Pack.Loaded;
            if (!_g.HasMoan && !pack) return "no moan available";
            if (!_g.VoiceOn) return "voice off in the game";
            if (!_g.InSexMode && !_g.HasLibrary && !pack) return "no own voice clips (outside sex mode)";
            if (!facePauseOk && (_g.FunctionPause || _g.FacialLayer == "off")) return "game face paused";
            if (_g.DialoguePlaying) return _g.DialogueWhy.Length > 0 ? _g.DialogueWhy : "dialogue";
            bool slap = tier >= 3;
            bool cut = tier >= 4 && I >= 0.5f;
            if (!slap && _g.SexFacialPlaying) return "sex facial";
            if (!slap && _g.DirtyTalkPause) return "dirty talk pause";
            // the game pads its moan clips to 4 s: a source with only the silent tail left (MoanTail) is free; our breath one-shot is invisible to the source's isPlaying
            if (((_g.MoanPlaying && !_g.MoanTail) || _g.OwnVoicePlaying) && !cut) return "moan playing";
            if (_g.DirtyTalkPlaying && (breath || tier <= 2)) return "dirty talk";
            if (gap)
            {
                if (breath) { if (_now - _lastBreathT < 4f) return "breath gap"; }
                else
                {
                    float g = VoiceGap[Mathf.Clamp(Tier, 0, 4)] * Mathf.Max(0.1f, _s.Cooldown) / 1.5f;
                    g = Mathf.Max(g, Mathf.Min(_g.LastClipLength, 1.8f));   // the game pads its moan clips to 4 s: only the first 1.8 s count
                    if (cut) g = Mathf.Min(g, 1f);
                    if (_now - _lastVoiceT < g) return "voice gap";
                }
            }
            return null;
        }

        // ---- 0.8.7: her face clips ----

        // Body.ApplyAll calls this for her before the bone sliders: the face clip of this frame over the Animator's face.
        public void FaceStep(float dt)
        {
            if (_s == null || !_s.On || !_s.Face || !_s.FaceClips || !_g.Found) return;
            // the mouth stays the game's while she speaks or moans (the lip sync is in the game's clips)
            bool mouthFree = !((_g.MoanPlaying && !_g.MoanTail) || _g.OwnVoicePlaying || _g.DirtyTalkPlaying || _g.DialoguePlaying);
            _face.Step(dt, mouthFree, _g.SexFacialPlaying ? 0.6f : 1f);
            // 0.9.1: a voice pack's clip has no lip sync of the game's: her jaw follows its loudness (relative to a slow peak follower, so quiet
            // and loud packs both move it; fast open 30 ms, close 90 ms)
            float lv = _g.PackLevel();
            float target = 0f;
            if (lv >= 0f)
            {
                _jawPeak = Mathf.Max(lv, Mathf.Max(0.02f, _jawPeak - 0.05f * _jawPeak * dt));
                float db = 20f * Mathf.Log10(Mathf.Max(lv, 1e-5f) / _jawPeak);   // 0 at the recent peak
                target = Mathf.Clamp01((db + 24f) / 22f);
            }
            _jawOpen = Mathf.MoveTowards(_jawOpen, target, (target > _jawOpen ? 1f / 0.03f : 1f / 0.09f) * dt);
            // 0.9.9: a clip with an Audio2Face curve drives the whole mouth instead of the loudness jaw
            bool lip = LipFrame(dt);
            if (lip) _jawOpen = 0f;
            else if (_jawOpen > 0.001f) _face.JawStep(_jawOpen * 0.75f);
        }

        // ---- 0.9.9: lip sync from baked Audio2Face curves (LipSync.cs) ----
        readonly float[] _lipW = new float[LipSync.K], _lipPrev = new float[LipSync.K];
        UnityEngine.Object _lipClip; LipSync.Curve _lipCurve; float _lipT, _lipFade, _lipLead; bool _lipPack;
        public string LipStatus = "";
        public int LipPreview = -1; public float LipPreviewW = 1f;   // the mouth pose editor's preview (shape index, weight)
        int _lipLogged;

        // True while the lip layer writes her mouth this frame (a curve, its 80 ms fade-out, or the editor's preview).
        bool LipFrame(float dt)
        {
            if (!_face.LipReady) { _lipFade = 0f; _lipClip = null; _lipCurve = null; return false; }
            LipSync.LoadMap(false);
            // 0.11.7: no arkit-map.txt yet (the user's PC in 0.11.4): seed the poses from her face clips once and save them, so the curves move
            // her lips out of the box instead of nothing but the loudness jaw; the editor refines them
            // 0.11.11: a map from before 0.11.11 holds whole clip frames (curled lower lip, 7 shapes); 0.11.35: one from 0.11.11-0.11.38 was built in a
            // mouth frame whose up pointed forward (lower lip pulled off her teeth): set aside and rebuilt
            string oldMap = "";
            if (!_autoSeeded && _s.LipSync && LipSync.RetireOldMap(out oldMap))
                L("lip sync: the mouth poses from an older version were set aside as " + oldMap + " (built in a wrong mouth frame: lower lip off her teeth); building new ones");
            // 0.11.45: a map from 0.11.39-0.11.44 has no brow / nose-sneer poses: build just those (the rest stays as it is) and save
            if (!_newShapesChecked && _s.LipSync && LipSync.PosedShapes > 0)
            {
                _newShapesChecked = true;
                if (LipSync.Map[LipSync.BrowFirst].Count == 0)
                {
                    string r2;
                    int n2 = _face.Seed(true, -1, out r2);
                    if (n2 > 0) LipSync.SaveMap();
                    L("lip sync: added the brow / nose-sneer poses to the saved mouth poses: " + r2);
                }
            }
            if (!_autoSeeded && _s.LipSync && LipSync.PosedShapes == 0 && !System.IO.File.Exists(LipSync.MapPath))
            {
                _autoSeeded = true;
                string r;
                int n = _face.Seed(true, -1, out r);
                if (n > 0) LipSync.SaveMap();
                L("lip sync: no mouth poses yet: " + (n > 0 ? "built " + n + " shapes in her mouth's frame and saved them to " + LipSync.MapPath + " (refine them in the Reactions tab's editor)" : "could not build them") + (r.Length > 0 ? "; " + r : ""));
            }
            if (LipPreview >= 0 && LipPreview < LipSync.K)
            {
                System.Array.Clear(_lipW, 0, LipSync.K); _lipW[LipPreview] = Mathf.Clamp01(LipPreviewW);
                LipStatus = "previewing " + LipSync.Shapes[LipPreview];
                bool pw = _face.LipStep(_lipW, 1f, _s.LipOverGame, _s.LipMood);
                if (pw) FaceCurves.Lip(_lipW, 1f);   // 0.11.47: the curve-driven wrinkles see the previewed shape too
                return pw;
            }
            bool active = false;
            if (_s.LipSync)
            {
                UnityEngine.Object clip; float time, pitch; bool pack;
                if (_g.VoiceNow(out clip, out time, out pitch, out pack))
                {
                    if (!ReferenceEquals(clip, _lipClip))
                    {
                        _lipClip = clip; _lipPack = pack; _lipT = time; _lipLead = 0f;
                        _lipCurve = null;
                        string key;
                        if (pack)
                        {
                            string path; float lead;
                            if (ReactionsGame.Pack.Source(clip, out path, out lead)) { _lipCurve = LipSync.ForPack(ReactionsGame.Pack.Name, path); _lipLead = lead; }
                            key = ReactionsGame.Pack.Name + "\\" + clip.name;
                        }
                        else
                        {
                            int n = _g.ClipSamples(clip);
                            _lipCurve = LipSync.ForGame(clip.name, n);
                            key = LipSync.DumpName(clip.name, n);
                        }
                        LipStatus = (_lipCurve != null ? "curve for " : "no curve for ") + key + (_lipCurve != null ? " (" + _lipCurve.Seconds.ToString("0.0") + " s, peak jawOpen " + _lipCurve.PeakJaw.ToString("0.00") + ")" : "");
                        if (_s.Log >= 2 || _lipLogged < 30) { _lipLogged++; Plugin.Log.LogInfo("Lip sync: " + LipStatus); }
                    }
                    else
                    {
                        // AudioSource.time moves in audio-buffer steps: a smooth clock pulled toward it
                        _lipT += dt * pitch;
                        float err = time - _lipT;
                        if (Mathf.Abs(err) > 0.12f) _lipT = time; else _lipT += err * Mathf.Min(1f, 8f * dt);
                    }
                    if (_lipCurve != null)
                    {
                        // the pitch / formant filter (VoiceShift) delays her voice by about 45 ms while it is on: the mouth waits for it
                        float fx = Mathf.Abs(_s.VoicePitch - 1f) > 1e-3f || Mathf.Abs(_s.VoiceFormant - 1f) > 1e-3f ? 0.045f : 0f;
                        // 0.11.12: AudioSource.time is where the mixer reads, ahead of what reaches her ears by the output buffers
                        // 0.11.47 [Voice] LipAutoLead: the frame drawn now reaches her eyes about one frame later (render + the headset's
                        // compositor), the sound does not wait for it: lead by the smoothed frame time (~43 ms at 23 fps, ~11 ms at 90)
                        float autoLead = _s.LipAutoLead ? Mathf.Clamp(Time.smoothDeltaTime, 0f, 0.1f) * pitch : 0f;
                        // [Voice] LipFilter: the curve over the clip time this frame covers, not one instant of it (LipSync.SampleWindow)
                        _lipCurve.SampleWindow(_lipT + _lipLead + _s.LipLead - fx - _g.OutputLatency + autoLead, Mathf.Clamp(dt, 0f, 0.1f) * pitch, _s.LipFilter, _lipW);
                        float k = Mathf.Max(0f, _s.LipStrength);
                        // ARKit's mouthClose only means "lips together while the jaw is open": Audio2Face often gives more of it than of
                        // jawOpen (0.49 vs 0.16), which pushed her lower lip up past her neutral mouth into a pout
                        // 0.11.12: at most half of it (equal amounts closed the lips completely while the chin dropped: a jaw with no lips)
                        _lipW[LipSync.MouthClose] = Mathf.Min(_lipW[LipSync.MouthClose], 0.5f * _lipW[LipSync.JawOpen]);
                        // 0.11.45 sticky lips: when her jaw starts to open from closed lips, the lips cling together for ~120 ms before they
                        // part (lips together = mouthClose up to the opening, fading out), like real lips peeling apart
                        float jo = _lipW[LipSync.JawOpen];
                        if (jo < 0.05f) { _stickArmed = true; _stickT = 0f; }
                        else if (_stickArmed)
                        {
                            _stickT += dt;
                            float st = Mathf.Clamp01(_s.LipSticky) * Mathf.Clamp01(1f - _stickT / 0.12f);
                            if (st <= 0f) _stickArmed = false;
                            else _lipW[LipSync.MouthClose] = Mathf.Max(_lipW[LipSync.MouthClose], st * jo);
                        }
                        // 0.11.45: the brow channels at their own strength (over her expression's brows)
                        for (int i = LipSync.BrowFirst; i <= LipSync.BrowLast; i++) _lipW[i] *= Mathf.Max(0f, _s.LipBrows);
                        for (int i = 0; i < LipSync.K; i++) { _lipW[i] = Mathf.Clamp01(_lipW[i] * k); _lipPrev[i] = _lipW[i]; }
                        active = true;
                        if (_now < _lipProbeUntil && _now >= _lipProbeNext) { _lipProbeNext = _now + 0.25f; _lipProbeW = true; }
                    }
                }
                else { _lipClip = null; _lipCurve = null; }
            }
            _lipFade = Mathf.MoveTowards(_lipFade, active ? 1f : 0f, dt / 0.08f);
            if (_lipFade <= 0f)
            {
                if (_now < _lipProbeUntil && _now >= _lipProbeNext) { _lipProbeNext = _now + 0.5f; L("lip probe t " + (_lipProbeUntil - _now).ToString("0.0") + " s left: no curve playing (" + LipStatus + ")"); }
                return false;
            }
            // fading out: the last weights held. An empty map writes nothing: the loudness jaw stays.
            bool wrote = _face.LipStep(active ? _lipW : _lipPrev, _lipFade, _s.LipOverGame, _s.LipMood);
            // 0.11.47: the weights her mouth is driven by, for the curve-driven face wrinkles (FaceCurves -> Wrinkles.UpdateActs)
            if (wrote) FaceCurves.Lip(active ? _lipW : _lipPrev, _lipFade);
            if (_lipProbeW)
            {
                _lipProbeW = false;
                var sb = new System.Text.StringBuilder("lip probe at ").Append(_lipT.ToString("0.00")).Append(" s: ");
                for (int pick = 0, shown = 0; pick < 5; pick++)
                {
                    int best = -1; float bw = 0.02f;
                    for (int i = 0; i < LipSync.K; i++) if (_lipW[i] > bw && (_probeUsed & (1L << i)) == 0) { bw = _lipW[i]; best = i; }
                    if (best < 0) break;
                    _probeUsed |= 1L << best;
                    sb.Append(shown++ > 0 ? ", " : "").Append(LipSync.Shapes[best]).Append(' ').Append(bw.ToString("0.00"));
                }
                _probeUsed = 0;
                sb.Append(wrote ? " -> bones: " + _face.LipDeltaReport() : " -> nothing written (no mapped pose)");
                if (_face.LipClamped > 0) sb.Append(" (held back to 15 mm / 30 deg on ").Append(_face.LipClamped).Append(" bone moves: a pose is too strong)");
                L(sb.ToString());
            }
            return wrote;
        }
        float _lipProbeUntil = -1f, _lipProbeNext;
        bool _autoSeeded, _newShapesChecked;
        bool _stickArmed; float _stickT;
        bool _lipProbeW;
        long _probeUsed;

        // the voice pack's PlayPack asks this before the game's "moan" mouth trigger
        bool LipHasCurve(UnityEngine.Object clip)
        {
            if (_s == null || !_s.LipSync || !_face.LipReady || LipSync.PosedShapes == 0) return false;
            string path; float lead;
            return ReactionsGame.Pack.Source(clip, out path, out lead) && LipSync.ForPack(ReactionsGame.Pack.Name, path) != null;
        }

        // the Reactions tab's dump of the game's voice clips (works without Reactions on: only the game link is needed)
        public bool Dumping { get { return _g.Dumping; } }
        public string DumpStatus { get { return _g.DumpStatus; } }
        public void DumpVoice() { _g.StartDump(); }

        // Test: a voice pack moan (or line) with lip sync; the log says whether its curve was found.
        public void TestLip()
        {
            if (_s == null || !_s.On) { L("test lip sync: reactions are off"); return; }
            if (!ReactionsGame.Pack.Loaded) { L("test lip sync: no voice pack is on"); return; }
            // 0.11.7: a check of every link first (the user's 0.11.4 run had only the loudness jaw: the lip sync was never in that build)
            string pack = ReactionsGame.Pack.Name;
            string dir = System.IO.Path.Combine(LipSync.Dir, pack);
            int csv = 0;
            try { if (System.IO.Directory.Exists(dir)) csv = System.IO.Directory.GetFiles(dir, "*.csv", System.IO.SearchOption.AllDirectories).Length; } catch { }
            LipSync.LoadMap(false);
            L("test lip sync: switch " + (_s.LipSync ? "on" : "OFF") + ", face survey " + (_face.LipReady ? "done" : "NOT done (no lip sync until it is)")
              + ", curves for '" + pack + "': " + csv + " in " + dir + (csv == 0 ? " (NONE: bake them with tools/a2f and copy them here)" : "")
              + ", mouth poses " + LipSync.PosedShapes + " of " + LipSync.K + " shapes in " + LipSync.MapPath + (LipSync.PosedShapes == 0 ? " (EMPTY: Seed or sculpt them in the editor, else only the loudness jaw moves)" : ""));
            string what;
            bool ok = (ReactionsGame.Pack.HasLines && _g.PackLine(3, 2, out what)) || _g.PackVoice(VoicePack.MoanMid, 1f, true, false);
            _lipProbeUntil = _now + 3f; _lipProbeNext = _now;
            L("test lip sync: " + (ok ? "played, logging the weights and bone moves for 3 s" : "failed"));
        }

        // The face clip for a touch: pain on hard slaps, surprise on soft ones; caress / grope: pleasure by her excitement on erogenous zones,
        // ticklish (caress) or surprised (grope) elsewhere. Own gaps and chances; a stronger face (climax, pain) is not cut by a weaker one.
        void FaceClip(int zone, int tier, float I)
        {
            int m; float w, hold, gap, chance;
            if (tier >= 4) { m = ReactionsFace.Pain; w = Mathf.Lerp(0.6f, 1f, I); hold = tier >= 5 ? 1.6f : 1.0f; gap = 0.4f; chance = 1f; }
            else if (tier == 3) { m = ReactionsFace.Surprise; w = 0.7f; hold = 0.6f; gap = 1f; chance = 1f; }
            else if (Erogenous[zone])
            {
                if (_g.DirtyTalkPlaying || State == RState.Guarded) return;   // her line's own talking face carries it; guarded: no pleasure
                m = Tier >= 4 ? ReactionsFace.Pleasure3 : Tier >= 2 ? ReactionsFace.Pleasure2 : ReactionsFace.Pleasure1;
                w = tier == 2 ? 0.85f : 0.6f; hold = tier == 2 ? 1.6f : 1.2f; gap = 2.5f; chance = tier == 2 ? 0.7f : 0.35f + 0.1f * Tier;
            }
            else { m = tier == 2 ? ReactionsFace.Surprise : ReactionsFace.Ticklish; w = 0.6f; hold = 1f; gap = 3f; chance = 0.4f; }
            if (_now - _lastFaceClipT < gap) return;
            bool pleasure = m <= ReactionsFace.Pleasure3;
            if (pleasure && _now < _faceLock) return;
            if (!(chance >= 1f || Rand() < chance)) return;
            if (PlayFace(m, w, hold) && pleasure) _faceLock = _now + 2.5f + 3f * Rand();
        }

        // 0.9.0 (the VaM scene's faces mostly follow her voice, not the touch): when any moan of hers starts (the game's own too), a pleasure
        // face by her excitement, at most one per lockout; in the climax every moan renews the climax face.
        void VoiceFace()
        {
            bool v = (_g.MoanPlaying && !_g.MoanTail) || _g.OwnVoicePlaying;
            bool onset = v && !_voiceWas;
            _voiceWas = v;
            if (!onset || _s == null || !_s.Face || !_s.FaceClips || !_g.Found) return;
            if (State == RState.Climax) { if (_now - _lastFaceClipT >= 0.8f) PlayFace(ReactionsFace.Climax, 1f, 1.6f); return; }
            if (Tier < 1 || State == RState.Guarded || State == RState.Startled || _now < _faceLock || _now - _lastFaceClipT < 1f) return;
            int m = Tier >= 4 ? ReactionsFace.Pleasure3 : Tier >= 2 ? ReactionsFace.Pleasure2 : ReactionsFace.Pleasure1;
            _resp.Length = 0;
            if (PlayFace(m, 0.75f, 1.4f))
            {
                _faceLock = _now + 2.5f + 3f * Rand();
                if (_s.Log >= 2) L("face on her voice: " + _resp);
            }
        }

        bool PlayFace(int m, float w, float hold)
        {
            string why;
            string nm = ReactionsFace.MoodNames[m];
            if (_face.Play(m, w * Mathf.Clamp01(_s.FaceStrength), hold, out why)) { _lastFaceClipT = _now; Note("face " + nm + " '" + _face.CurrentName + "'"); return true; }
            if (why == "building") { _lastFaceClipT = _now; Note("face " + nm + " (its clip is being sampled)"); }
            else if (why == "hook") Note("face " + nm + " left to the game's own hook");
            else if (why != "busy" && why != "surveying") Note("face " + nm + " skipped (" + why + ")");
            return false;
        }

        // The panel's face tests: a mood's clip at full strength, the game's eye reaction at a level, the game's blink.
        public void TestFace(int m)
        {
            if (_s == null || !_s.On) { L("test face: reactions are off"); return; }
            if (!_s.Face || !_s.FaceClips) { L("test face: Face or Face clips is off"); return; }
            _resp.Length = 0; _face.Stop();
            bool ok = PlayFace(Mathf.Clamp(m, 0, ReactionsFace.NM - 1), 1f, 1.5f);
            L("test face " + ReactionsFace.MoodNames[Mathf.Clamp(m, 0, ReactionsFace.NM - 1)] + " (test): " + (ok ? "playing" : "not played") + (_resp.Length > 0 ? ": " + _resp : ""));
        }

        public void TestEye(int level)
        {
            if (_s == null || !_s.On) { L("test eye: reactions are off"); return; }
            _resp.Length = 0;
            int k = -1; bool fresh = false;
            bool ok = _g.Found && _g.EyeReact(Mathf.Clamp(level, 1, 3), out k, out fresh);
            if (ok && fresh) _face.Watch("eye reaction " + k, _now);
            L("test eye reaction Lv" + level + " (test): " + (ok ? (fresh ? "id " + k : "one is still running") : "not available (the face layer must be 'on' or 'eye')"));
        }

        public void TestBlink()
        {
            if (_s == null || !_s.On) { L("test blink: reactions are off"); return; }
            bool ok = _g.Found && _g.Blink();
            L("test blink (test): " + (ok ? "sent" : "failed"));
        }

        // ---- 0.8.6: her lines ----

        // Which lines a touch speaks: 0 nipple (breasts), 1 pussy / 2 anal (between her legs, front / back), 3 her general lines (butt, hips, thighs);
        // -1 none (chest, belly, arms, calves, face, neck keep their moans).
        int LineCatOf(int zone, HerBody.Seg g, Vector3 pt)
        {
            if (_penK >= 0) return _penK == Penetration.Vagina ? 1 : _penK == Penetration.Anus ? 2 : -1;   // 0.11.25: a finger in her mouth: no lines
            switch (zone)
            {
                case ZBreast: return 0;
                case ZButt: case ZThigh: return 3;
                case ZHips: return PelvisCat(g, pt);
            }
            return -1;
        }

        // The pelvis has no genital segment of its own: the touch point in the pelvis' own frame (across the hip joints, up the spine, back toward
        // the butt spheres) decides. Below the hip-joint line (3 cm slack) and between the thighs: front pussy, back anal; else her hips (general).
        int PelvisCat(HerBody.Seg g, Vector3 pt)
        {
            if (g == null || g.A == null || g.B == null || _root == null) return 3;
            Vector3 hl = g.A.position, hr = g.B.position, c = (hl + hr) * 0.5f, lat = hr - hl;
            float w = lat.magnitude;
            if (w < 1e-3f) return 3;
            lat /= w;
            var sp = HerBody.Find("spine");
            Vector3 up = sp != null && sp.A != null ? sp.A.position - c : _root.up;
            up = Vector3.ProjectOnPlane(up, lat);
            if (up.sqrMagnitude < 1e-6f) up = _root.up;
            up.Normalize();
            var bl = HerBody.Find("butt_L"); var br = HerBody.Find("butt_R");
            Vector3 back = bl != null && br != null && bl.A != null && br.A != null ? (SegCentre(bl) + SegCentre(br)) * 0.5f - c : -_root.forward;
            back = Vector3.ProjectOnPlane(Vector3.ProjectOnPlane(back, lat), up);
            if (back.sqrMagnitude < 1e-6f) back = Vector3.ProjectOnPlane(-_root.forward, up);
            if (back.sqrMagnitude < 1e-6f) return 3;
            back.Normalize();
            Vector3 d = pt - c;
            float below = -Vector3.Dot(d, up), side = Mathf.Abs(Vector3.Dot(d, lat)), front = -Vector3.Dot(d, back);
            if (below < -0.03f || side > 0.3f * w + 0.02f) return 3;
            return front >= 0f ? 1 : 2;
        }

        // The line's level from her excitement tier: 1-2 Lv1, 3 Lv2, 4 Lv3; tier 0 has none (she moans).
        static int LineLevelOf(int t) { return t >= 4 ? 3 : t >= 3 ? 2 : t >= 1 ? 1 : 0; }

        // The arbiter for lines and feeling sounds, stricter than the moans' (no slap goes through): never over dialogue, a kiss, the game's
        // feeling loops, its dirty talk or dirty-talk pause, or a moan still sounding. Lines need sex / foreplay mode and the game's dirty talk on
        // (the game mutes its dirty-talk source otherwise). test: no gaps.
        string LineBlock(bool feel, bool test, bool facePauseOk = false, bool pack = false)
        {
            if (!_g.Found) return "no game link";
            if (!_g.VoiceOn) return "voice off in the game";
            if (!feel && !pack && !_g.InSexMode) return "outside sex mode";
            if (!feel && !pack && !_g.DirtyTalkOn) return "dirty talk is off in the game";
            if (!facePauseOk && (_g.FunctionPause || _g.FacialLayer == "off")) return "game face paused";
            if (_g.DialoguePlaying) return _g.DialogueWhy.Length > 0 ? _g.DialogueWhy : "dialogue";
            if (_g.SexFacialPlaying) return "sex facial";
            if (_g.DirtyTalkPause) return "dirty talk pause";
            if (_g.DirtyTalkPlaying) return "dirty talk";
            if ((_g.MoanPlaying && !_g.MoanTail) || _g.OwnVoicePlaying) return "moan playing";
            if (!test)
            {
                if (_now - _lastVoiceT < 1f) return "voice gap";
                if (feel && _now - _lastEnterT < 10f) return "feeling gap";
            }
            return null;
        }

        // Seconds between two lines: 8 s at the default voice gap of 1.5 s, and never before the last line is over plus 2 s.
        float LineGap() { return Mathf.Max(8f * Mathf.Max(0.1f, _s.Cooldown) / 1.5f, _g.LastLineLength + 2f); }

        // A line for the touched area, if the zone has lines, she is excited (tier 1+), the line gap is over and the chance hits. True = it plays.
        bool TryLine(int zone, HerBody.Seg g, Vector3 pt, bool test)
        {
            int cat = LineCatOf(zone, g, pt);
            if (ReactionsGame.Pack.Loaded) return TryPackLine(cat, test);   // 0.9.1: the pack's own lines (the game's are another voice)
            if (cat < 0 || !_g.HasLines) return false;
            int lvl = LineLevelOf(Tier);
            if (lvl == 0 || _now - _lastLineT < LineGap()) return false;
            if (!(_s.LineChance >= 1f || Rand() < _s.LineChance)) return false;
            if (_g.Found) _g.ReadGatesNow(_now);   // forced: ReadGates itself only reads every 0.1 s
            string b = LineBlock(false, test);
            if (b != null) { Skip("line: " + b); Note("line skipped (" + b + ")"); return false; }
            string how;
            if (!_g.SpeakLine(cat, lvl, _s.LinesGamePath, out how)) { Skip("line failed"); Note("line failed (" + how + ")"); return false; }
            _lastLineT = _now; _lastVoiceT = _now;
            Note("line " + ReactionsGame.LineCats[_g.LineCat] + " Lv" + _g.LineLevel + " '" + (_g.LastLineClip != null ? _g.LastLineClip.name : "?") + "' via " + how);
            return true;
        }

        // 0.9.1: a line from the voice pack: the area's bank at her level (an area without lines, and chest / belly / ..., her general lines);
        // at excitement tier 4 sometimes asking for more (request), at tier 1-2 outside sex mode sometimes inviting (invite). Same gaps and
        // chance as the game's lines; needs neither sex mode nor the game's dirty talk (it plays on the moan source).
        bool TryPackLine(int cat, bool test)
        {
            var pk = ReactionsGame.Pack;
            if (!pk.HasLines) return false;
            int lvl = LineLevelOf(Tier);
            if (lvl == 0 || (!test && _now - _lastLineT < LineGap())) return false;
            if (!test && !(_s.LineChance >= 1f || Rand() < _s.LineChance)) return false;
            if (_g.Found) _g.ReadGatesNow(_now);
            string b = LineBlock(false, test, false, true);
            if (b != null) { Skip("line: " + b); Note("line skipped (" + b + ")"); return false; }
            string what; bool ok;
            if (Tier >= 4 && pk.HasState(VoicePack.SRequest) && Rand() < 0.35f) ok = _g.PackState(VoicePack.SRequest, out what);
            else if (Tier <= 2 && !_g.InSexMode && pk.HasState(VoicePack.SInvite) && Rand() < 0.25f) ok = _g.PackState(VoicePack.SInvite, out what);
            else ok = _g.PackLine(cat < 0 ? 3 : cat, lvl, out what);
            if (!ok) { Skip("line failed"); Note("line failed (pack)"); return false; }
            _lastLineT = _now; _lastVoiceT = _now;
            Note("line " + what + " (pack)");
            return true;
        }

        // A state line from the pack (guard, afterglow): own 6 s gap, the lines' arbiter. True = it plays.
        bool TryStateLine(int st, string why)
        {
            var pk = ReactionsGame.Pack;
            if (_s == null || !_s.Voice || !_s.Lines || !pk.HasState(st) || _now - _lastStateLineT < 6f) return false;
            if (_g.Found) _g.ReadGatesNow(_now);
            string b = LineBlock(false, false, false, true);
            if (b != null && b != "voice gap") { Skip("state line: " + b); return false; }
            string what;
            if (!_g.PackState(st, out what)) return false;
            _lastStateLineT = _now; _lastLineT = _now; _lastVoiceT = _now; _vPlayed++;
            if (_s.Log >= 1) L("line " + what + " (pack, " + why + ")");
            return true;
        }

        // A Feeling_enter sound for reaching excitement tier t (1..4 -> Feeling_enter_0..3). True = it plays.
        bool TryFeeling(int t, bool test)
        {
            if (!_g.HasFeeling || t < 1 || ReactionsGame.Pack.Loaded) return false;
            if (_g.Found) _g.ReadGatesNow(_now);
            string b = LineBlock(true, test);
            if (b != null) { Note("feeling skipped (" + b + ")"); return false; }
            int n = Mathf.Clamp(t - 1, 0, 3);
            if (!_g.Feeling(n)) { Note("feeling failed"); return false; }
            _lastEnterT = _now; _lastVoiceT = _now;
            Note("feeling enter " + n + " (tier " + t + ")");
            return true;
        }

        // The game's own touch moan, or our own clip when the game's would do nothing (its dirty-talk pause is up): then dirty talk is stopped
        // like the game's DirtyTalkMoan does. cut: a moan still sounding is stopped first.
        bool PlayMoan(bool cut, int tier)
        {
            int bank = Tier <= 1 ? 0 : Tier <= 3 ? 1 : 2;
            float pitch = 1f + (Rand() * 2f - 1f) * _s.VoicePitchVar;
            // 0.9.0: a voice pack carries every moan: exertion on hard slaps, climax in the climax, else low / mid / high by her excitement
            if (ReactionsGame.Pack.Loaded)
            {
                int c = tier >= 6 ? VoicePack.Climax : tier >= 4 ? VoicePack.Exertion : bank == 0 ? VoicePack.MoanLow : bank == 1 ? VoicePack.MoanMid : VoicePack.MoanHigh;
                if ((cut || _g.MoanTail) && (_g.MoanPlaying || _g.OwnVoicePlaying)) _g.StopMoan();
                // 0.9.1: a hard slap sometimes says it ("ouch", "be gentle") instead of the exertion, when lines are on
                string what;
                if (tier >= 4 && tier < 6 && _s.Lines && ReactionsGame.Pack.HasState(VoicePack.SOuch) && Rand() < 0.4f && _g.PackState(VoicePack.SOuch, out what))
                { _lastVoiceT = _now; _resetAt = _now + 1f; _moanKind = "pack line " + what + (cut ? ", cut" : ""); return true; }
                if (!_g.PackVoice(c, pitch, _g.DirtyTalkPlaying, !_face.HasJaw)) return false;
                _lastVoiceT = _now; _resetAt = _now + 1f;
                _moanKind = "pack " + VoicePack.Folders[c] + (cut ? ", cut" : "");
                return true;
            }
            bool pause = _g.DirtyTalkPause, sex = _g.InSexMode;
            // outside sex / foreplay mode our own clip plays and the Animator "moan" trigger is set too (the lips move; the 1 s reset in
            // ReactionsGame stops it from staying queued)
            bool viaGame = sex && !pause && (_s.VoiceMode == 0 || _g.DirtyTalkPlaying);   // MoanFunction does nothing while the pause is up
            if ((cut || _g.MoanTail) && (_g.MoanPlaying || _g.OwnVoicePlaying)) _g.StopMoan();   // a cut, or only the silent padding of the last clip is left
            bool ok = viaGame ? _g.Moan() : _g.MoanClip(bank, pitch, _g.DirtyTalkPlaying, true);
            if (!ok) ok = viaGame ? _g.MoanClip(bank, pitch, _g.DirtyTalkPlaying, true) : (sex && !pause && _g.Moan());
            if (ok)
            {
                _lastVoiceT = _now; _resetAt = _now + 1f;
                _moanKind = viaGame ? (_s.VoiceMode == 1 && !_g.DirtyTalkPlaying ? "moan (by intensity)" : "moan") : (!sex ? "moan (own clip, outside sex mode)" : pause ? "moan (own clip, the game's pause is up)" : "moan (by intensity)");
                if (cut) _moanKind += ", cut";
            }
            return ok;
        }

        // The face (design 3.2): the game's own eye reaction (ReactionFacialEnter_Eye / _Out_Eye with an id from its dirty-talk face library:
        // soft / hard / too hard), also under a moan, and alongside it a wince on hard hits or a blink on soft ones when no moan carries the
        // face. The feeling loops (sexFacialPlaying) do not stop it.
        void DoFace(int tier, float I, bool voiced)
        {
            if (!_g.Found || _g.FunctionPause || _g.DialoguePlaying || _g.FacialLayer == "off" || _now - _lastFaceT < 0.5f) return;
            int k; bool fresh;
            // the eye reaction runs alongside the wince / blink, not instead of it (the game never calls its own exit, ours does at 0.6-1.0 s)
            if (_g.EyeReact(tier >= 5 ? 3 : tier >= 4 ? 2 : 1, out k, out fresh)) { _lastFaceT = _now; if (fresh) { Note("eye reaction " + k); _face.Watch("eye reaction " + k, _now); } }
            if (voiced) return;                                      // a moan carries the face (the Animator "moan" trigger comes with it)
            if (tier >= 4 && I >= 0.3f && _g.InSexMode) { if (_g.WinceTrigger()) { _lastFaceT = _now; _resetAt = _now + 1f; Note("wince"); } }
            else if (_g.Blink()) { _lastFaceT = _now; Note("blink"); }
        }

        // A soft face for caresses and gropes: the eye reaction (Lv1 / Lv2 ids) if the face layer allows it, else the game's blink. True = something happened.
        bool FaceSoft(int level)
        {
            if (!_g.Found || _g.FunctionPause || _g.DialoguePlaying || _g.FacialLayer == "off" || _now - _lastFaceT < 0.5f) { Note("face skipped (paused or busy)"); return false; }
            int k; bool fresh;
            if (_g.EyeReact(level, out k, out fresh)) { _lastFaceT = _now; if (fresh) { Note("eye reaction " + k); _face.Watch("eye reaction " + k, _now); } return true; }
            if (_g.Blink()) { _lastFaceT = _now; Note("blink"); return true; }
            Note("face skipped (no eye reaction or blink)");
            return false;
        }

        // The flinch (design 3.3): an angular impulse on the limb through Overlap and a velocity kick on her soft zones.
        void DoFlinch(int zone, HerBody.Seg g, int tier, float I, Vector3 pt, Vector3 dir, bool guarded, bool force)
        {
            if (!force && _now - _lastFlinchT < 0.25f) return;
            _lastFlinchT = _now;
            float scale = Mathf.Lerp(1f, 0.6f, E) * (guarded ? 1.5f : 1f) * (tier == 5 ? 1.5f : tier == 3 ? 0.5f : 1f);
            float ns = KickNs[zone] * I * scale;
            if (ns > 0f && _ov != null && HerBody.Built && _s.LimbPush > 0f)
            {
                int ki = g.Index;
                if (zone == ZButt)
                {
                    int side = _sideOf[g.Index];
                    var th = HerBody.Find(ThighNames[side == 1 ? 1 : 0]);
                    if (th != null) ki = th.Index;
                }
                _ov.Kick(ki, pt, dir * ns);
                Note("flinch " + ns.ToString("0.00") + " N*s");
            }
            if (SoftKick[zone] && _her != null && _her.Soft != null)
            {
                _her.Soft.Kick(pt, dir, 0.4f * I, 0.08f);
                Note("jiggle");
            }
            _flinch++;
        }

        // The slap sound (design 3.4): our own source at the spot.
        void DoSlap(int tier, float I, Vector3 pt, bool force)
        {
            if (!_g.Found || (!force && !_g.SfxOn) || _now - _lastSlapT < 0.25f) return;
            _lastSlapT = _now;
            // 0.9.0: the slap files by strength (dry recordings: little pitch spread), else the game's clips below
            if (_s.Foley)
            {
                int cat = tier >= 5 ? SkinFoley.SlapHard : tier == 4 ? (I >= 0.55f ? SkinFoley.SlapHard : SkinFoley.SlapMedium) : (I >= 0.3f ? SkinFoley.SlapMedium : SkinFoley.SlapSoft);
                float fv = _s.Volume * Mathf.Lerp(0.55f, 1f, I);
                if (_g.FoleyShot(cat, pt, fv, Mathf.Lerp(0.95f, 1.05f, Rand()) - (tier >= 5 ? 0.04f : 0f)))
                { _sfx++; _slapFail = false; Note("slap " + SkinFoley.Folders[cat] + " " + fv.ToString("0.0")); return; }
            }
            float vol = _s.Volume * Mathf.Lerp(0.6f, 1f, I) * 1.6f;   // PlayOneShot's scale may exceed 1
            float pitch = Mathf.Lerp(0.85f, 1.15f, Rand()) + (tier == 3 ? 0.05f : tier >= 4 ? -0.05f : 0f);
            if (_g.Slap(pt, vol, pitch, tier >= 4, false)) { _sfx++; _slapFail = false; Note("slap " + vol.ToString("0.0")); }
            else _slapFail = true;
        }

        // ================= states (design 2.2) =================

        void EnterState(RState st, float duration)
        {
            if (st == State) { _until = _now + duration; return; }
            RState old = State;
            // 0.9.1: the pack's state lines: guarded (at once, if the voice is free) and afterglow (1.5-3 s after the climax ends)
            if (st == RState.Guarded) TryStateLine(VoicePack.SGuard, "guarded");
            if (st == RState.Afterglow && old == RState.Climax) _afterLineAt = _now + 1.5f + 1.5f * Rand();
            State = st; _until = duration > 0f ? _now + duration : 0f; _guardHold = 0f;
            _stChanges++;
            if (_s != null && _s.Log >= 1)
            {
                if (_now - _lastStateLog >= 2f)   // at most one line per 2 s; the rest is counted in the 10 s summary
                {
                    _lastStateLog = _now;
                    L("state " + StateNames[(int)old] + " -> " + StateNames[(int)st] + " (E " + E.ToString("0.00") + ", P " + P.ToString("0.00") + ")");
                }
                else _stSuppressed++;
            }
        }

        void EnterClimax()
        {
            _climaxT0 = _now; _climaxIdx = 0; EnterState(RState.Climax, 4f);
            if (_s != null && _s.Face && _s.FaceClips) { _resp.Length = 0; PlayFace(ReactionsFace.Climax, 1f, 3.4f); }
        }

        // back from a timed state: guarded again if she was and the pain is still up, else by excitement
        void Derive()
        {
            if (_wasGuarded && P >= 0.15f) { _wasGuarded = false; EnterState(RState.Guarded, 0f); return; }
            _wasGuarded = false;
            EnterState(E >= 0.2f ? RState.Aroused : RState.Idle, 0f);
        }

        // The panel's state buttons: jump to a state with the given excitement and pain.
        public void SetState(RState st, float e, float p)
        {
            E = Mathf.Clamp01(e); P = Mathf.Clamp01(p); SetTier();
            _enterTier = Tier;                 // a jump by button is not a rise (no feeling sound)
            _wasGuarded = false;
            _lastEvT = _now;
            switch (st)
            {
                case RState.Startled: EnterState(st, 1.5f); _until = _now + 1.5f; break;
                case RState.Climax: _climaxT0 = _now; _climaxIdx = 0; EnterState(st, 4f); _until = _now + 4f; break;
                case RState.Afterglow: EnterState(st, 12f); _until = _now + 12f; _nextBreathAt = _now + 4f; break;
                default: EnterState(st, 0f); break;
            }
        }

        // ================= per-frame bookkeeping =================

        void Tick(float dt, ReactionSettings s)
        {
            // decay
            if (_now - Mathf.Max(_lastEvT, _lastStimT) > 10f) E = Mathf.Max(0f, E - 0.02f * dt);
            P = Mathf.Max(0f, P - 0.02f * dt);
            SetTier();
            if (Tier < _enterTier) _enterTier = Tier;   // fallen back: the next rise is greeted again

            // states
            // the timed states always expire (also with AutoState off); only the automatic entries below need AutoState
            switch (State)
            {
                case RState.Startled: if (_now >= _until) Derive(); break;
                case RState.Climax: if (_now >= _until) { E = 0.25f; SetTier(); EnterState(RState.Afterglow, 12f); _nextBreathAt = _now + 4f; } break;
                case RState.Afterglow: if (_now >= _until) Derive(); break;
            }
            if (s.AutoState)
            {
                switch (State)
                {
                    case RState.Guarded: if (P < 0.15f) Derive(); break;
                    case RState.Startled: case RState.Climax: case RState.Afterglow: break;
                    default:
                        if (P >= 0.4f && E < 0.4f)
                        {
                            _guardHold += dt;
                            if (_guardHold >= 3f) { EnterState(RState.Guarded, 0f); break; }
                        }
                        else _guardHold = 0f;
                        EnterState(E >= 0.2f ? RState.Aroused : RState.Idle, 0f);
                        break;
                }
                if (s.AllowClimax && E >= 1f && State != RState.Climax && State != RState.Afterglow) EnterClimax();
            }
            VoiceFace();
            if (State == RState.Climax)
            {
                // three moans, if the arbiter lets them through
                while (_climaxIdx < 3 && _now - _climaxT0 >= ClimaxAt[_climaxIdx])
                {
                    _climaxIdx++;
                    if (s.Voice && _g.Found)
                    {
                        _g.ReadGatesNow(_now);   // forced: ReadGates itself only reads every 0.1 s
                        // 0.8.6: with lines on, the climax opens with her strongest feeling sound (Feeling_enter_3)
                        // 0.9.0 (0.8.8 log: the second climax had no feeling sound, "game face paused"): with our climax face on her, a paused game
                        // face doesn't stop the climax voice (its face is ours); everything else still does
                        bool ours = _face.CurrentMood == ReactionsFace.Climax;
                        if (_climaxIdx == 1 && s.Lines && _g.HasFeeling && !ReactionsGame.Pack.Loaded && LineBlock(true, true, ours) == null && _g.Feeling(3)) { _lastVoiceT = _now; _lastEnterT = _now; _vPlayed++; continue; }
                        string b = VoiceBlock(5, false, false, 0f, ours);
                        if (b == null && PlayMoan(false, 6)) { _lastVoiceT = _now; _resetAt = _now + 1f; _vPlayed++; }
                        else Skip(b ?? "voice failed");
                    }
                }
            }
            if (_afterLineAt > 0f && _now >= _afterLineAt)
            {
                _afterLineAt = 0f;
                if (State == RState.Afterglow && TryStateLine(VoicePack.SAfterglow, "afterglow")) _nextBreathAt = Mathf.Max(_nextBreathAt, _now + _g.LastLineLength + 2f);
            }
            if (State == RState.Climax) { }
            else if (State == RState.Afterglow && s.Voice && _g.Found && _now >= _nextBreathAt)
            {
                _nextBreathAt = _now + 4f + Rand() * 2f;
                _g.ReadGatesNow(_now);   // forced: ReadGates itself only reads every 0.1 s
                if (VoiceBlock(1, true, true, 0f) == null && (ReactionsGame.Pack.Loaded ? _g.PackBreath(true) : _g.Breath())) { _lastBreathT = _now; _vPlayed++; }
            }

            // delayed second blink of a startle
            if (_blink2At > 0f && _now >= _blink2At) { _blink2At = 0f; if (s.Face && _g.Found && !_g.DialoguePlaying && !_g.FunctionPause) _g.Blink(); }

            // blush: target = gain * (0.35 E + flush) (+ 0.3 in the climax), attack 2/s, fall 1/s
            _flush = Mathf.Max(0f, _flush - 0.03f * dt);
            // as a fraction of the material's _BlushPower range: E = 1 reaches BlushGain (60 % by default) of it, slaps and the climax add on top
            float target = s.Blush ? Mathf.Clamp01(s.BlushGain * (E + (_flush + (State == RState.Climax ? 0.3f : 0f)) / 0.35f)) : 0f;
            _blushCur = Mathf.MoveTowards(_blushCur, target, (target > _blushCur ? 2f : 1f) * dt);
            if (_blushCur > 0f || _blushSent)
            {
                _g.SetBlush(_blushCur * _g.BlushRange, s.BlushBody ? _blushCur * 0.5f * _g.BlushRange : 0f);
                _blushSent = _blushCur > 0f;
            }

            // gaze away: up in 0.1 s, held, down over 0.5 s
            bool hold = s.Gaze && _now < _gazeHold;
            _away = hold ? Mathf.MoveTowards(_away, 1f, 10f * dt) : Mathf.MoveTowards(_away, 0f, dt / 0.5f);
            if (_away > 0f || _gazeSent) { _g.SetGaze(_away); _gazeSent = _away > 0f; }

            // 10 s summary while anything happened
            if (_now >= _nextSummary)
            {
                _nextSummary = _now + 10f;
                int total = 0; for (int i = 1; i < 6; i++) total += _cnt[i];
                if (total > 0 && s.Log >= 1)
                {
                    _sb.Length = 0;
                    _sb.Append("last 10 s: ").Append(total).Append(" events (");
                    bool first = true;
                    for (int i = 1; i < 6; i++) if (_cnt[i] > 0) { if (!first) _sb.Append(", "); first = false; _sb.Append(_cnt[i]).Append(' ').Append(TierNames[i]); }
                    _sb.Append("), voice ").Append(_vPlayed).Append(" played / ").Append(_vSkipped).Append(" skipped");
                    if (_vSkipped > 0)
                    {
                        _sb.Append(" (");
                        bool f2 = true;
                        foreach (var kv in _why) if (kv.Value > 0) { if (!f2) _sb.Append(", "); f2 = false; _sb.Append(kv.Key).Append(' ').Append(kv.Value); }
                        _sb.Append(')');
                    }
                    _sb.Append(", sound ").Append(_sfx).Append(", flinch ").Append(_flinch);
                    if (_stChanges > 0) _sb.Append(", state changes ").Append(_stChanges).Append(" (").Append(_stSuppressed).Append(" not logged)");
                    _sb.Append("; E ").Append(E.ToString("0.00")).Append(", P ").Append(P.ToString("0.00")).Append(", state ").Append(StateNames[(int)State]);
                    L(_sb.ToString());
                }
                System.Array.Clear(_cnt, 0, _cnt.Length); _vPlayed = _vSkipped = _sfx = _flinch = 0; _stChanges = _stSuppressed = 0;
                foreach (var key in new System.Collections.Generic.List<string>(_why.Keys)) _why[key] = 0;
            }
        }

        // ================= strings for the panel =================

        void Display(ReactionSettings s)
        {
            // Bars: rebuilt when the rounded percentages change
            int e = (int)(E * 100f + 0.5f), p = (int)(P * 100f + 0.5f);
            if (e != _barE || p != _barP)
            {
                _barE = e; _barP = p;
                _sb.Length = 0;
                _sb.Append("excitement ["); AppendBar(e); _sb.Append("] ").Append(e).Append("%    pain ["); AppendBar(p); _sb.Append("] ").Append(p).Append('%');
                Bars = _sb.ToString();
            }
            // StateLine: the state and tier, and whether she is being touched
            bool active = _now - _lastStimT <= 0.8f;
            int slk = ((int)State * 8 + Tier) * 2 + (active ? 1 : 0);
            if (slk != _slKey)
            {
                _slKey = slk;
                _sb.Length = 0;
                switch (State)
                {
                    case RState.Idle: _sb.Append("Idle"); break;
                    case RState.Aroused: _sb.Append("Aroused ").Append(Tier); break;
                    case RState.Startled: _sb.Append("Startled"); break;
                    case RState.Guarded: _sb.Append("Guarded"); break;
                    case RState.Climax: _sb.Append("Climax"); break;
                    default: _sb.Append("Afterglow"); break;
                }
                if (active) _sb.Append(", being touched");
                StateLine = _sb.ToString();
            }
            // Status: rebuilt when a flag changes
            int key = (_g.Found ? 1 : 0) | (_g.HasMoan ? 2 : 0) | (_g.VoiceOn ? 4 : 0) | (_g.InSexMode ? 8 : 0) | (s.Voice ? 16 : 0) | (s.Face ? 32 : 0) | (s.Flinch ? 64 : 0)
                | (s.LimbPush > 0f ? 128 : 0) | (s.Sound ? 256 : 0) | (s.Blush ? 512 : 0) | (s.Gaze ? 1024 : 0) | (_slapFail ? 2048 : 0) | (HerBody.Built ? 4096 : 0)
                | (s.VoiceMode == 1 ? 8192 : 0) | (_g.SfxOn ? 16384 : 0)
                | (s.Lines ? 1 << 25 : 0) | (_g.HasLines ? 1 << 26 : 0) | (_g.DirtyTalkOn ? 1 << 27 : 0) | (_g.LinePathOk ? 1 << 28 : 0) | (s.LinesGamePath ? 1 << 29 : 0);
            key ^= ((int)(_g.Desire + 0.5f) & 0x1FF) << 15;
            bool noHand = !PhysicsHands.Active && !s.PushByTouch;
            if (noHand) key |= 1 << 24;
            if (key != _stKey)
            {
                _stKey = key;
                _sb.Length = 0;
                if (!HerBody.Built) _sb.Append("off: her body is not built (turn on the soft zones or the physics hands)");
                else
                {
                    _sb.Append("on: ");
                    if (!s.Voice) _sb.Append("voice off");
                    else if (!_g.Found) _sb.Append("voice off (the game's voice was not found: ").Append(_g.Missing).Append(')');
                    else if (!_g.HasMoan) _sb.Append("voice off (no moan found)");
                    else if (!_g.VoiceOn) _sb.Append("voice idle (voice is off in the game)");
                    else if (!_g.InSexMode) _sb.Append(_g.HasLibrary ? "voice ok (own clips and breath outside sex mode)" : "voice idle (outside sex mode and no own voice clips)");
                    else _sb.Append(s.VoiceMode == 1 ? "voice ok (by intensity)" : "voice ok");
                    _sb.Append(s.Face ? ", face" : ", face off");
                    if (!s.Flinch) _sb.Append(", flinch off"); else if (s.LimbPush <= 0f) _sb.Append(", flinch (jiggle only: push is 0)"); else _sb.Append(", flinch");
                    if (!s.Voice || !s.Lines) _sb.Append(", lines off");
                    else if (!_g.Found) { }
                    else if (!_g.HasLines) _sb.Append(", lines off (no line clips found)");
                    else if (!_g.DirtyTalkOn) _sb.Append(", lines idle (dirty talk is off in the game)");
                    else _sb.Append(!s.LinesGamePath ? ", lines (own clips: the game's path is off)" : _g.LinePathOk ? ", lines" : ", lines (own clips, no face)");
                    if (!s.Sound) _sb.Append(", sound off"); else if (_slapFail) _sb.Append(", sound off (no clips)"); else _sb.Append(", sound");
                    _sb.Append(s.Blush ? ", blush" : ", blush off");
                    _sb.Append(s.Gaze ? "; gaze on" : "; gaze off");
                    if (_g.Found && _g.Desire >= 0f) _sb.Append("; game desire ").Append((int)(_g.Desire + 0.5f));
                    if (noHand) _sb.Append(" (no hand is tracked: turn on physics hands or 'Other hands push too')");
                }
                Status = _sb.ToString();
            }
        }

        void AppendBar(int pct)
        {
            int n = Mathf.Clamp(pct / 5, 0, 20);
            for (int i = 0; i < 20; i++) _sb.Append(i < n ? '#' : '-');
        }

        // ================= tests (the panel's buttons) =================

        HerBody.Seg FindSeg(int zone, int side)
        {
            var segs = HerBody.Segs;
            HerBody.Seg any = null;
            for (int i = 0; i < segs.Count; i++)
            {
                var g = segs[i];
                if (g.Index < 0 || g.Index >= _zoneOf.Length || _zoneOf[g.Index] != zone) continue;
                if (_sideOf[g.Index] == side || _sideOf[g.Index] < 0) return g;
                if (any == null) any = g;
            }
            return any;
        }

        Vector3 TestDir(int zone, int side, Vector3 centre)
        {
            switch (HitFrom[zone])
            {
                case 0: return -_root.forward;
                case 1: return _root.forward;
                case 2:
                    {
                        Vector3 o = Vector3.ProjectOnPlane(centre - _root.position, _root.up);
                        if (o.sqrMagnitude < 1e-6f) o = side == 0 ? -_root.right : _root.right;
                        return -o.normalized;
                    }
            }
            return side == 0 ? _root.right : -_root.right;   // her left is -right: a hit on her left side moves toward +right
        }

        // The panel's test buttons: a reaction of `tier` (1 caress .. 5 too hard) on `zone` (ZoneNames) at side 0 left / 1 right.
        public void Test(int zone, int side, int tier)
        {
            if (_s == null || _her == null || _root == null || !_s.On) { L("test: reactions are off"); return; }
            if (!HerBody.Built) { L("test: her body is not built"); return; }
            zone = Mathf.Clamp(zone, 0, NZ - 1); tier = Mathf.Clamp(tier, 1, 5); side = side == 1 ? 1 : 0;
            var g = FindSeg(zone, side);
            if (g == null) { L("test: no " + ZoneNames[zone] + " segment"); return; }
            if (tier >= 3 && _now - _zArr[zone] < ArrCool[zone]) { L("test: " + ZoneNames[zone] + " is in its cooldown"); return; }
            Vector3 c = SegCentre(g);
            float ti = tier >= 3 ? Mathf.Clamp01((TestV[tier] - 1.2f) / 3.3f) : TestI[tier];   // the slaps' continuous curve at 50 % sensitivity
            Fire(zone, g, tier, ti, c, TestDir(zone, side, c), "the test button", TestV[tier], tier == 2 ? 30f : 3f, true);
        }

        // The panel's line test: a line for the test zone at her excitement's level (at least Lv1), no chance and no gaps, the arbiter otherwise.
        public void TestLine(int zone, int side)
        {
            if (_s == null || _her == null || _root == null || !_s.On) { L("test line: reactions are off"); return; }
            if (!HerBody.Built) { L("test line: her body is not built"); return; }
            zone = Mathf.Clamp(zone, 0, NZ - 1); side = side == 1 ? 1 : 0;
            var g = FindSeg(zone, side);
            if (g == null) { L("test line: no " + ZoneNames[zone] + " segment"); return; }
            int cat = LineCatOf(zone, g, SegCentre(g));
            if (cat < 0) { L("test line: " + ZoneNames[zone] + " has no lines (breast, hips, butt and thighs do)"); return; }
            int lvl = Mathf.Max(1, LineLevelOf(Tier));
            if (_g.Found) _g.ReadGatesNow(_now);
            string b = LineBlock(false, true), res, how;
            if (b != null) res = "blocked (" + b + ")";
            else if (_g.SpeakLine(cat, lvl, _s.LinesGamePath, out how)) { _lastLineT = _now; _lastVoiceT = _now; res = "played " + ReactionsGame.LineCats[_g.LineCat] + " Lv" + _g.LineLevel + " '" + (_g.LastLineClip != null ? _g.LastLineClip.name : "?") + "' via " + how; }
            else res = "failed (" + how + ")";
            L("test line on " + ZoneNames[zone] + " (" + ReactionsGame.LineCats[cat] + " Lv" + lvl + ", test): " + res);
            LastLine = "last: test line on " + ZoneNames[zone] + " -> " + res;
        }

        // 0 moan, 1 breath, 2 wince, 3 slap sound, 4 flinch butt, 5 feeling sound (her tier's, at least Feeling_enter_0)
        // the panel's experimental Animator steppers (one write per press)
        public bool SetAnimFloat(string name, float v) { return _g.SetAnimFloat(name, v); }

        public void TestResponse(int which)
        {
            if (_s == null || _her == null || _root == null || !_s.On) { L("test: reactions are off"); return; }
            _resp.Length = 0;
            string res = "";
            switch (which)
            {
                case 0:
                case 1:
                    {
                        if (_g.Found) _g.ReadGatesNow(_now);   // forced: ReadGates itself only reads every 0.1 s
                        string block = VoiceBlock(Mathf.Max(Tier, 3), which == 1, false, 0f);
                        if (block != null) res = "blocked (" + block + ")";
                        else if (which == 1) { bool ok = ReactionsGame.Pack.Loaded ? _g.PackBreath(Tier >= 2) : _g.Breath(); if (ok) _lastBreathT = _now; res = ok ? "played" : "failed"; }
                        else { bool ok = PlayMoan(false, 2); res = ok ? "played" : "failed"; }
                        break;
                    }
                case 2:
                    {
                        bool ok = _g.Found && _g.WinceTrigger();
                        if (ok) { _lastFaceT = _now; _resetAt = _now + 1f; }
                        res = ok ? "sent" : "failed";
                        break;
                    }
                case 5:
                    {
                        bool ok = TryFeeling(Mathf.Max(1, Tier), true);
                        res = (ok ? "played: " : "not played: ") + (_resp.Length > 0 ? _resp.ToString()
                            : ReactionsGame.Pack.Loaded ? "a voice pack is on (the game's feeling sounds stay off with a pack)" : "no feeling clip found");
                        break;
                    }
                case 3:
                    {
                        var g = FindSeg(ZButt, 0); Vector3 at = g != null ? SegCentre(g) : _root.position;
                        bool ok = _g.Found && ((_s.Foley && _g.FoleyShot(SkinFoley.SlapHard, at, _s.Volume, 1f)) || _g.Slap(at, _s.Volume * 1.6f, 1f, true, true));
                        res = ok ? "played" : "failed";
                        break;
                    }
                case 6:
                case 8:
                    {
                        // 0.11.7: the skin sounds alone, at the butt (6 pat, 8 grab), with the Skin sounds volume
                        var g = FindSeg(ZButt, 0); Vector3 at = g != null ? SegCentre(g) : _root.position;
                        bool ok = _g.Found && _g.FoleyShot(which == 6 ? SkinFoley.Pat : SkinFoley.Grab, at, _s.SkinVolume * 0.8f, 1f);
                        res = (ok ? "played" : "failed") + " (" + (which == 6 ? "pat" : "grab") + ", volume " + (_s.SkinVolume * 0.8f).ToString("0.00") + (_s.Sound ? "" : "; Sound is off") + ")";
                        break;
                    }
                case 9:
                case 10:
                    {
                        // 0.11.25: 9 the penetration sound alone at her vagina; 10 a whole in-stroke there (sound, excitement, voice)
                        int k = Penetration.Vagina;
                        var g = HerBody.Find("pelvis");
                        Vector3 at = Penetration.Ok[k] ? Penetration.O[k] : g != null ? SegCentre(g) : _root.position;
                        if (which == 10)
                        {
                            if (!HerBody.Built || g == null) { res = "her body is not built"; break; }
                            _pen[k].LastSnd = -100f;
                            PenEvent(k, 1, 0.2f, at, "the test button", _s);
                            res = _resp.Length > 0 ? _resp.ToString() : "see the penetration line above";
                            break;
                        }
                        float vol = _s.SkinVolume * _s.PenVolume * 0.8f;
                        bool ok = _g.Found && _g.FoleyShot(SkinFoley.Penetration, at, vol, 1f);
                        res = (ok ? "played" : "failed") + " (penetration, volume " + vol.ToString("0.00") + (_s.Sound ? "" : "; Sound is off")
                            + (ok || !ReactionsGame.Foley.Loaded || ReactionsGame.Foley.HasFiles(SkinFoley.Penetration) ? "" : "; no files in the penetration folder") + ")";
                        break;
                    }
                case 7:
                    {
                        var g = FindSeg(ZButt, 0);
                        _testRubAt = g != null ? SegCentre(g) : _root.position; _testRubUntil = _now + 1.5f;
                        res = _s.Foley && _s.Sound ? "rub for 1.5 s (volume " + (0.8f * _s.SkinVolume * 0.7f).ToString("0.00") + ")" : "off: Sound or Skin sounds is off";
                        break;
                    }
                default:
                    {
                        if (!HerBody.Built) { res = "her body is not built"; break; }
                        var g = FindSeg(ZButt, 0);
                        if (g == null) { res = "no butt segment"; break; }
                        Vector3 c = SegCentre(g);
                        DoFlinch(ZButt, g, 4, 0.8f, c, TestDir(ZButt, 0, c), false, true);
                        res = _resp.Length > 0 ? _resp.ToString() : "nothing moved (limb push is 0?)";
                        break;
                    }
            }
            L("test response " + which + " (test): " + res);
        }
    }
}
