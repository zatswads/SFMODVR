using System.Text;
using UnityEngine;

namespace SfMorph
{
    // Her reactions to touch (0.7.9): what the Body hands the director each frame. Filled from the config once per frame (no allocation).
    internal class ReactionSettings
    {
        public bool PushByTouch, On, Voice, Face, Flinch, Sound, Blush, BlushBody, Gaze, FeedDesire, AutoState, AllowClimax, VoiceGuard;
        public float Sensitivity, Cooldown, Volume, BlushGain, VoicePitchVar, LimbPush;
        public int VoiceMode, Log;

        public void Fill(Plugin p)
        {
            On = p.ReactOn.Value; Voice = p.ReactVoice.Value; Face = p.ReactFace.Value; Flinch = p.ReactFlinch.Value; Sound = p.ReactSound.Value;
            Blush = p.ReactBlush.Value; BlushBody = p.ReactBlushBody.Value; Gaze = p.ReactGaze.Value; FeedDesire = p.ReactFeedDesire.Value;
            AutoState = p.ReactAuto.Value; AllowClimax = p.ReactClimax.Value; VoiceGuard = p.VoiceGuard.Value;
            Sensitivity = p.ReactSensitivity.Value; Cooldown = p.ReactCooldown.Value; Volume = p.ReactVolume.Value;
            BlushGain = p.ReactBlushGain.Value; VoicePitchVar = p.ReactVoicePitchVar.Value;
            VoiceMode = p.ReactVoiceMode.Value; Log = p.ReactLog.Value;
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
        public string Status = "", StateLine = "", Bars = "", GuardStatus = "";
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
        static readonly float[] TestI = { 0f, 0.15f, 0.6f, 0.6f, 0.8f, 1f };
        static readonly float[] TestV = { 0f, 0f, 0f, 1.6f, 3.2f, 5.5f };

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
        readonly System.Random _rng = new System.Random();
        readonly StringBuilder _sb = new StringBuilder(160), _resp = new StringBuilder(160);

        // set when the reactions part of Update threw (logged once); Body turns ReactOn off and clears it. The voice guard is not affected.
        public bool Failed;
        bool _failLogged;
        Character _her; Transform _root; ReactionSettings _s; Overlap _ov;
        float _now, _nextRefresh, _nextGate, _nextSummary;
        float _lastEvT = -100f, _lastStimT = -100f, _lastVoiceT = -100f, _lastBreathT = -100f, _lastFlinchT = -100f, _lastSlapT = -100f, _lastFaceT = -100f, _lastDesireT = -100f;
        float _resetAt, _blink2At, _until, _guardHold, _climaxT0, _nextBreathAt, _lastStateLog = -100f;
        int _climaxIdx; bool _wasGuarded;
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
            if (_now >= _nextRefresh) { _nextRefresh = _now + 1f; _g.Refresh(her, _now); }
            if (_g.Found && !_bridgeLogged) { _bridgeLogged = true; if (_g.BridgeLine != null && _g.BridgeLine.Length > 0) L(_g.BridgeLine); }
            else if (!_g.Found) _bridgeLogged = false;       // a re-found bridge is logged again
            _g.Guard(s.VoiceGuard, _now);
            GuardStatus = _g.GuardStatus != null ? _g.GuardStatus : "";
            if (_resetAt > 0f && _now >= _resetAt) { _resetAt = 0f; _g.ResetMoan(); }

            if (!s.On)
            {
                if (_wasOn) { _wasOn = false; GiveBack(); ResetSession(); }
                Status = "off: reactions are off"; StateLine = ""; Bars = ""; _stKey = int.MinValue; _slKey = int.MinValue; _barE = -1;
                return;
            }
            _wasOn = true;
            try
            {
                if (_now >= _nextGate) { _nextGate = _now + 0.1f; _g.ReadGates(_now); }

                if (HerBody.Built)
                {
                    if (HerBody.BuildVersion != _bv) RebuildMap();
                    if (cut) ClearR();
                    else Classify(dt, s);
                }
                else if (_bv != -2) { _bv = -2; ClearR(); }

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

        // Gives back everything we changed (blush, look-at weight, the guard's hooks) and forgets the session.
        public void Release()
        {
            try { if (_resetAt > 0f) _g.ResetMoan(); } catch { }     // the delayed "moan" trigger safety must not be lost
            try { _g.Release(); } catch { }
            ResetSession();
            _wasOn = false; _bridgeLogged = false; _nextRefresh = 0f; _resetAt = 0f;
            _her = null; _root = null; _s = null; _ov = null;
            _evN = 0; System.Array.Clear(_cnt, 0, _cnt.Length); _vPlayed = _vSkipped = _sfx = _flinch = 0; _nextSummary = 0f;
            _slapFail = false; _bv = -1;
            Status = ""; StateLine = ""; Bars = ""; GuardStatus = "";
            _stKey = int.MinValue; _slKey = int.MinValue; _barE = -1; _barP = -1;
        }

        // The listeners' values we wrote go back (blush, look-at), once.
        void GiveBack()
        {
            if (_resetAt > 0f) { _resetAt = 0f; try { _g.ResetMoan(); } catch { } }
            if (_blushSent) { _blushSent = false; _g.SetBlush(0f, 0f); }
            if (_gazeSent) { _gazeSent = false; _g.SetGaze(0f); }
            _blushCur = 0f; _away = 0f;
        }

        // Everything per-session about her mood and the classifier is forgotten (a cut, a new her, Reactions turned off).
        void ResetSession()
        {
            E = 0f; P = 0f; Tier = 0; State = RState.Idle; _wasGuarded = false;
            _flush = 0f; _gazeHold = 0f; _guardHold = 0f; _until = 0f; _climaxIdx = 3; _blink2At = 0f;
            _lastEvT = -100f; _lastStimT = -100f; _lastVoiceT = -100f; _lastBreathT = -100f; _lastFlinchT = -100f; _lastSlapT = -100f; _lastFaceT = -100f; _lastDesireT = -100f;
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
            var segs = HerBody.Segs;
            for (int i = 0; i < segs.Count; i++)
                if (segs[i].Index >= 0 && segs[i].Index < _r.Length) _r[segs[i].Index].LastHitRead = segs[i].HitT;
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
                    {
                        float I = t == 3 ? (v - 1.2f * k) / (1.1f * k) : t == 4 ? (v - 2.3f * k) / (2.2f * k) : 1f;
                        Fire(zone, g, t, Mathf.Clamp(I, 0.1f, 1f), pt, dir, by, v, force, false);
                    }
                }

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
                bool low = force < 10f && g.GeoDepth < 0.005f;
                _r[gi].LowT = low ? _r[gi].LowT + dt : 0f;
                if (_r[gi].LowT >= 0.5f) _r[gi].Pressed = false;
                bool fired = false;
                if (pressing && _r[gi].PressT >= 0.25f && !_r[gi].Pressed && _now - _zGrope[zone] >= 2f && _now - _zArr[zone] >= 0.5f)
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
                case RState.Guarded: case RState.Afterglow: return 0.5f;
                case RState.Startled: case RState.Climax: return 0f;
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
            _resp.Length = 0;
            _zAny[zone] = _now; if (tier >= 3) _zArr[zone] = _now;

            // excitement and pain
            E = Mathf.Clamp01(E + ZoneGain[zone][Mathf.Min(tier, 4) - 1] * TierFactor[tier] * StateFactor());
            float dP = tier == 4 ? 0.15f * I * (zone == ZFace ? 2f : 1f) : tier == 5 ? 0.35f : (tier == 2 && zone == ZNeck ? 0.1f : 0f);
            P = Mathf.Clamp01(P + dP);
            _lastEvT = _now; _lastStimT = _now;
            SetTier();
            _cnt[tier]++;

            // states that start with an event
            if (s.AutoState && tier >= 4 && State != RState.Climax)
            {
                _wasGuarded = wasGuarded || (State == RState.Startled && _wasGuarded);
                EnterState(RState.Startled, 1.5f);
                if (s.Face) _blink2At = _now + 0.4f;
            }

            // voice
            bool voiced = false;
            if (s.Voice)
            {
                float chance = tier == 1 ? 0.15f + 0.1f * Tier : tier == 2 ? 0.5f + 0.1f * Tier : tier == 3 ? 0.6f + 0.1f * Tier : 1f;
                if (tier <= 2 && (wasGuarded || State == RState.Startled)) chance = 0f;
                if (test) chance = 1f;
                if (chance >= 1f || Rand() < chance)
                {
                    if (_g.Found) _g.ReadGatesNow(_now);   // forced: ReadGates itself only reads every 0.1 s
                    string block = VoiceBlock(tier, tier == 1, true);
                    if (block != null) { _vSkipped++; Note("voice skipped (" + block + ")"); }
                    else
                    {
                        bool ok;
                        if (tier == 1) { ok = _g.Breath(); if (ok) _lastBreathT = _now; }
                        else ok = PlayMoan();
                        if (ok) { voiced = true; _vPlayed++; Note(tier == 1 ? "breath" : (s.VoiceMode == 1 && !_g.DirtyTalkPlaying ? "moan (by intensity)" : "moan")); }
                        else { _vSkipped++; Note("voice failed"); }
                    }
                }
            }
            if (!voiced && tier >= 3 && s.Face) DoFace(tier);

            // flinch, slap sound, blush, gaze, the game's desire
            if (tier >= 3)
            {
                if (s.Flinch) DoFlinch(zone, g, tier, I, pt, dir, wasGuarded, false);
                if (s.Sound) DoSlap(tier, I, pt, false);
                if (s.Blush)
                {
                    float add = FlushAdd[tier] * I * (zone == ZFace ? 2f : 1f);
                    _flush = Mathf.Min(0.65f, _flush + add);
                    Note("blush +" + (add * s.BlushGain).ToString("0.00"));
                }
                if (s.Gaze && (tier >= 4 || wasGuarded)) _gazeHold = Mathf.Max(_gazeHold, _now + (tier >= 5 ? 1.5f : 0.4f));
            }
            if (s.FeedDesire && tier >= 2 && _g.InSexMode && _now - _lastDesireT >= 3f) { _lastDesireT = _now; _g.FeedDesire(); Note("desire"); }

            if (s.AutoState && s.AllowClimax && E >= 1f && State != RState.Climax && State != RState.Afterglow) EnterClimax();

            // log
            _evN++;
            bool logIt = s.Log >= 2 || (s.Log == 1 && (test || _evN <= 20 || _evN % 10 == 0));
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
        }

        // The arbiter (design 3.1): null = the voice may play, else why not. gap: also apply the tier gap.
        string VoiceBlock(int tier, bool breath, bool gap)
        {
            if (!_g.Found) return "no game link";
            if (!_g.HasMoan) return "no moan available";
            if (!_g.VoiceOn) return "voice off in the game";
            if (!_g.InSexMode) return "not in sex or foreplay mode";
            if (_g.FunctionPause || _g.FacialLayer == "off") return "game face paused";
            if (_g.DialoguePlaying) return "dialogue";
            if (_g.DirtyTalkPause) return "dirty talk pause";
            if (_g.MoanPlaying || _g.OwnVoicePlaying) return "moan playing";   // our breath one-shot is invisible to the source's isPlaying
            if (_g.DirtyTalkPlaying && (breath || tier <= 2)) return "dirty talk";
            if (gap)
            {
                if (breath) { if (_now - _lastBreathT < 4f) return "breath gap"; }
                else
                {
                    float g = VoiceGap[Mathf.Clamp(Tier, 0, 4)] * Mathf.Max(0.1f, _s.Cooldown) / 1.5f;
                    g = Mathf.Max(g, Mathf.Max(_g.TouchReactionTime, _g.LastClipLength + 0.2f));
                    if (_now - _lastVoiceT < g) return "voice gap";
                }
            }
            return null;
        }

        bool PlayMoan()
        {
            int bank = Tier <= 1 ? 0 : Tier <= 3 ? 1 : 2;
            float pitch = 1f + (Rand() * 2f - 1f) * _s.VoicePitchVar;
            bool viaGame = _s.VoiceMode == 0 || _g.DirtyTalkPlaying;   // over dirty talk only the game's own moan
            bool ok = viaGame ? _g.Moan() : _g.MoanClip(bank, pitch);
            if (!ok && !_g.DirtyTalkPlaying) ok = viaGame ? _g.MoanClip(bank, pitch) : _g.Moan();
            if (ok) { _lastVoiceT = _now; _resetAt = _now + 1f; }
            return ok;
        }

        // Face without voice (design 3.2): a wince on hard hits, a blink on soft slaps.
        void DoFace(int tier)
        {
            if (!_g.Found || _g.FunctionPause || _g.DialoguePlaying || _g.FacialLayer == "off" || _now - _lastFaceT < 0.5f) return;
            if (tier >= 4) { if (_g.WinceTrigger()) { _lastFaceT = _now; _resetAt = _now + 1f; Note("wince"); } }
            else if (_g.Blink()) { _lastFaceT = _now; Note("blink"); }
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
            float vol = _s.Volume * Mathf.Lerp(0.35f, 1f, I) * (tier >= 4 ? 1f : 0.7f);
            float pitch = Mathf.Lerp(0.85f, 1.15f, Rand()) + (tier == 3 ? 0.05f : tier >= 4 ? -0.05f : 0f);
            _lastSlapT = _now;
            if (_g.Slap(pt, vol, pitch)) { _sfx++; _slapFail = false; Note("slap " + vol.ToString("0.0")); }
            else _slapFail = true;
        }

        // ================= states (design 2.2) =================

        void EnterState(RState st, float duration)
        {
            if (st == State) { _until = _now + duration; return; }
            RState old = State;
            State = st; _until = duration > 0f ? _now + duration : 0f; _guardHold = 0f;
            if (_s != null && _s.Log >= 1 && _now - _lastStateLog >= 1f)
            {
                _lastStateLog = _now;
                L("state " + StateNames[(int)old] + " -> " + StateNames[(int)st] + " (E " + E.ToString("0.00") + ", P " + P.ToString("0.00") + ")");
            }
        }

        void EnterClimax() { _climaxT0 = _now; _climaxIdx = 0; EnterState(RState.Climax, 4f); }

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
            if (State == RState.Climax)
            {
                // three moans, if the arbiter lets them through
                while (_climaxIdx < 3 && _now - _climaxT0 >= ClimaxAt[_climaxIdx])
                {
                    _climaxIdx++;
                    if (s.Voice && _g.Found)
                    {
                        _g.ReadGatesNow(_now);   // forced: ReadGates itself only reads every 0.1 s
                        string b = VoiceBlock(5, false, false);
                        if (b == null && PlayMoan()) { _lastVoiceT = _now; _resetAt = _now + 1f; _vPlayed++; }
                        else _vSkipped++;
                    }
                }
            }
            else if (State == RState.Afterglow && s.Voice && _g.Found && _now >= _nextBreathAt)
            {
                _nextBreathAt = _now + 4f + Rand() * 2f;
                _g.ReadGatesNow(_now);   // forced: ReadGates itself only reads every 0.1 s
                if (VoiceBlock(1, true, true) == null && _g.Breath()) { _lastBreathT = _now; _vPlayed++; }
            }

            // delayed second blink of a startle
            if (_blink2At > 0f && _now >= _blink2At) { _blink2At = 0f; if (s.Face && _g.Found && !_g.DialoguePlaying && !_g.FunctionPause) _g.Blink(); }

            // blush: target = gain * (0.35 E + flush) (+ 0.3 in the climax), attack 2/s, fall 1/s
            _flush = Mathf.Max(0f, _flush - 0.03f * dt);
            float target = s.Blush ? Mathf.Clamp01(s.BlushGain * (0.35f * E + _flush + (State == RState.Climax ? 0.3f : 0f))) : 0f;
            _blushCur = Mathf.MoveTowards(_blushCur, target, (target > _blushCur ? 2f : 1f) * dt);
            if (_blushCur > 0f || _blushSent)
            {
                _g.SetBlush(_blushCur, s.BlushBody ? _blushCur * 0.5f : 0f);
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
                    _sb.Append("), voice ").Append(_vPlayed).Append(" played / ").Append(_vSkipped).Append(" skipped, sound ").Append(_sfx).Append(", flinch ").Append(_flinch)
                       .Append("; E ").Append(E.ToString("0.00")).Append(", P ").Append(P.ToString("0.00")).Append(", state ").Append(StateNames[(int)State]);
                    L(_sb.ToString());
                }
                System.Array.Clear(_cnt, 0, _cnt.Length); _vPlayed = _vSkipped = _sfx = _flinch = 0;
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
                | (s.VoiceMode == 1 ? 8192 : 0) | (_g.SfxOn ? 16384 : 0);
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
                    else if (!_g.InSexMode) _sb.Append("voice idle (not in sex or foreplay mode)");
                    else _sb.Append(s.VoiceMode == 1 ? "voice ok (by intensity)" : "voice ok");
                    _sb.Append(s.Face ? ", face" : ", face off");
                    if (!s.Flinch) _sb.Append(", flinch off"); else if (s.LimbPush <= 0f) _sb.Append(", flinch (jiggle only: push is 0)"); else _sb.Append(", flinch");
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
            Fire(zone, g, tier, TestI[tier], c, TestDir(zone, side, c), "the test button", TestV[tier], tier == 2 ? 30f : 3f, true);
        }

        // 0 moan, 1 breath, 2 wince, 3 slap sound, 4 flinch butt
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
                        string block = VoiceBlock(Mathf.Max(Tier, 3), which == 1, false);
                        if (block != null) res = "blocked (" + block + ")";
                        else if (which == 1) { bool ok = _g.Breath(); if (ok) _lastBreathT = _now; res = ok ? "played" : "failed"; }
                        else { bool ok = PlayMoan(); res = ok ? "played" : "failed"; }
                        break;
                    }
                case 2:
                    {
                        bool ok = _g.Found && _g.WinceTrigger();
                        if (ok) { _lastFaceT = _now; _resetAt = _now + 1f; }
                        res = ok ? "sent" : "failed";
                        break;
                    }
                case 3:
                    {
                        var g = FindSeg(ZButt, 0); Vector3 at = g != null ? SegCentre(g) : _root.position;
                        bool ok = _g.Found && _g.Slap(at, _s.Volume, 1f);
                        res = ok ? "played" : "failed";
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
