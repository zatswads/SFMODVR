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
        static readonly float[] KickNs = { 0.4f, 0.6f, 0.6f, 0f, 1.2f, 1.0f, 0.6f, 0.6f, 0.5f, 0.4f };
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
        bool _quiet;                       // set while a weaker hit of the same swing fires: no voice, sound or wince, only the rest
        string _moanKind = "moan";
        // the hits of one frame: a swing touches several segments at once and only the strongest is voiced / heard
        struct Hit { public int Zone, Tier; public HerBody.Seg G; public float I, V, Force; public Vector3 Pt, Dir; public string By; }
        readonly Hit[] _hits = new Hit[24];
        int _nHits;
        readonly System.Collections.Generic.Dictionary<string, int> _why = new System.Collections.Generic.Dictionary<string, int>();
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
            _g.Pump(_now);
            _g.DiagTick(_now);                                       // the Diagnose button's state machine (idle unless it was pressed)
            _g.Guard(s.VoiceGuard, _now);
            GuardStatus = _g.GuardStatus != null ? _g.GuardStatus : "";
            if (_resetAt > 0f && _now >= _resetAt) { _resetAt = 0f; _g.ResetMoan(); }

            if (!s.On)
            {
                if (_wasOn) { _wasOn = false; GiveBack(); ResetSession(); }
                Status = "off: reactions are off"; StateLine = ""; Bars = ""; LastLine = ""; _stKey = int.MinValue; _slKey = int.MinValue; _barE = -1;
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

        // ---- the Diagnose button (see ReactionsGame.DiagStart) ----
        public bool BridgeFound { get { return _g.Found; } }
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
            try { _g.RestorePitch(); } catch { }                       // the moan source gets its own pitch back
            _blushCur = 0f; _away = 0f;
        }

        // Everything per-session about her mood and the classifier is forgotten (a cut, a new her, Reactions turned off).
        void ResetSession()
        {
            E = 0f; P = 0f; Tier = 0; State = RState.Idle; _wasGuarded = false;
            _flush = 0f; _gazeHold = 0f; _guardHold = 0f; _until = 0f; _climaxIdx = 3; _blink2At = 0f;
            _lastEvT = -100f; _lastStimT = -100f; _lastVoiceT = -100f; _lastBreathT = -100f; _lastFlinchT = -100f; _lastSlapT = -100f; _lastFaceT = -100f; _lastDesireT = -100f; _lastCaressFaceT = -100f;
            _startledAt = -100f; _nHits = 0; _quiet = false;
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
                        Collect(zone, g, t, Mathf.Clamp01((v - 1.2f * k) / (3.3f * k)), pt, dir, by, v, force);   // one continuous strength over all slap tiers
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
                bool pressing = force >= 20f * k || g.GeoDepth >= 0.015f * k;
                _r[gi].PressT = pressing ? _r[gi].PressT + dt : 0f;
                bool low = force < 10f && g.GeoDepth < 0.005f;
                _r[gi].LowT = low ? _r[gi].LowT + dt : 0f;
                if (_r[gi].LowT >= 0.5f) _r[gi].Pressed = false;
                bool fired = false;
                if (pressing && _r[gi].PressT >= 0.25f && !_r[gi].Pressed && _now - _zGrope[zone] >= 2f && _now - _zArr[zone] >= 0.5f && !Pending(zone))
                {
                    _r[gi].Pressed = true; _zGrope[zone] = _now; fired = true;
                    if (ZoneGain[zone][1] > 0f)
                    {
                        float I = Mathf.Max(Mathf.Clamp01((force - 20f * k) / 40f), Mathf.Clamp01(g.GeoDepth / 0.04f));
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
                float chance = tier == 1 ? 0.15f + 0.1f * Tier : tier == 2 ? 0.5f + 0.1f * Tier : tier == 3 ? 0.6f + 0.1f * Tier : 1f;
                if (tier <= 2 && (wasGuarded || State == RState.Startled)) chance = 0f;
                if (test) chance = 1f;
                if (_quiet) { if (tier >= 3) { Skip("same swing"); Note("voice skipped (same swing)"); } }
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
                        if (tier == 1) { ok = _g.Breath(); if (ok) _lastBreathT = _now; _moanKind = "breath"; }
                        else ok = PlayMoan(cut);
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
                if (s.Gaze && (tier >= 4 || wasGuarded)) _gazeHold = Mathf.Max(_gazeHold, _now + (tier >= 5 ? 1.5f : 0.4f));
            }
            // the game's desire: groping and slaps (every 3 s); a caress only once she is well aroused (excitement tier 3) and every 6 s
            if (s.FeedDesire && (tier >= 2 || (tier == 1 && Tier >= 3)) && _g.InSexMode && _now - _lastDesireT >= (tier == 1 ? 6f : 3f)) { _lastDesireT = _now; _g.FeedDesire(); Note("desire"); }

            if (s.AutoState && s.AllowClimax && E >= 1f && State != RState.Climax && State != RState.Afterglow) EnterClimax();

            // log
            // (only the voiced best hit of a swing is the event: the quiet ones of the same swing show up in the log at Log 2 only)
            if (!_quiet) _evN++;
            bool logIt = _quiet ? s.Log >= 2 : s.Log >= 2 || (s.Log == 1 && (test || _evN <= 20 || _evN % 10 == 0));
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
            _last.Append("last: ").Append(TierNames[tier]).Append(" on ").Append(ZoneNames[zone]).Append(" by ").Append(by != null ? by : "a hand");
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
        string VoiceBlock(int tier, bool breath, bool gap, float I)
        {
            if (!_g.Found) return "no game link";
            if (!_g.HasMoan) return "no moan available";
            if (!_g.VoiceOn) return "voice off in the game";
            if (!_g.InSexMode && !_g.HasLibrary) return "no own voice clips (outside sex mode)";
            if (_g.FunctionPause || _g.FacialLayer == "off") return "game face paused";
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

        // The game's own touch moan, or our own clip when the game's would do nothing (its dirty-talk pause is up): then dirty talk is stopped
        // like the game's DirtyTalkMoan does. cut: a moan still sounding is stopped first.
        bool PlayMoan(bool cut)
        {
            int bank = Tier <= 1 ? 0 : Tier <= 3 ? 1 : 2;
            float pitch = 1f + (Rand() * 2f - 1f) * _s.VoicePitchVar;
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
            if (_g.EyeReact(tier >= 5 ? 3 : tier >= 4 ? 2 : 1, out k, out fresh)) { _lastFaceT = _now; if (fresh) Note("eye reaction " + k); }
            if (voiced) return;                                      // a moan carries the face (the Animator "moan" trigger comes with it)
            if (tier >= 4 && I >= 0.3f && _g.InSexMode) { if (_g.WinceTrigger()) { _lastFaceT = _now; _resetAt = _now + 1f; Note("wince"); } }
            else if (_g.Blink()) { _lastFaceT = _now; Note("blink"); }
        }

        // A soft face for caresses and gropes: the eye reaction (Lv1 / Lv2 ids) if the face layer allows it, else the game's blink. True = something happened.
        bool FaceSoft(int level)
        {
            if (!_g.Found || _g.FunctionPause || _g.DialoguePlaying || _g.FacialLayer == "off" || _now - _lastFaceT < 0.5f) { Note("face skipped (paused or busy)"); return false; }
            int k; bool fresh;
            if (_g.EyeReact(level, out k, out fresh)) { _lastFaceT = _now; if (fresh) Note("eye reaction " + k); return true; }
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
            float vol = _s.Volume * Mathf.Lerp(0.6f, 1f, I) * 1.6f;   // PlayOneShot's scale may exceed 1
            float pitch = Mathf.Lerp(0.85f, 1.15f, Rand()) + (tier == 3 ? 0.05f : tier >= 4 ? -0.05f : 0f);
            _lastSlapT = _now;
            if (_g.Slap(pt, vol, pitch, tier >= 4, false)) { _sfx++; _slapFail = false; Note("slap " + vol.ToString("0.0")); }
            else _slapFail = true;
        }

        // ================= states (design 2.2) =================

        void EnterState(RState st, float duration)
        {
            if (st == State) { _until = _now + duration; return; }
            RState old = State;
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
                        string b = VoiceBlock(5, false, false, 0f);
                        if (b == null && PlayMoan(false)) { _lastVoiceT = _now; _resetAt = _now + 1f; _vPlayed++; }
                        else Skip(b ?? "voice failed");
                    }
                }
            }
            else if (State == RState.Afterglow && s.Voice && _g.Found && _now >= _nextBreathAt)
            {
                _nextBreathAt = _now + 4f + Rand() * 2f;
                _g.ReadGatesNow(_now);   // forced: ReadGates itself only reads every 0.1 s
                if (VoiceBlock(1, true, true, 0f) == null && _g.Breath()) { _lastBreathT = _now; _vPlayed++; }
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
                    else if (!_g.InSexMode) _sb.Append(_g.HasLibrary ? "voice ok (own clips and breath outside sex mode)" : "voice idle (outside sex mode and no own voice clips)");
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
            float ti = tier >= 3 ? Mathf.Clamp01((TestV[tier] - 1.2f) / 3.3f) : TestI[tier];   // the slaps' continuous curve at 50 % sensitivity
            Fire(zone, g, tier, ti, c, TestDir(zone, side, c), "the test button", TestV[tier], tier == 2 ? 30f : 3f, true);
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
                        string block = VoiceBlock(Mathf.Max(Tier, 3), which == 1, false, 0f);
                        if (block != null) res = "blocked (" + block + ")";
                        else if (which == 1) { bool ok = _g.Breath(); if (ok) _lastBreathT = _now; res = ok ? "played" : "failed"; }
                        else { bool ok = PlayMoan(false); res = ok ? "played" : "failed"; }
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
                        bool ok = _g.Found && _g.Slap(at, _s.Volume * 1.6f, 1f, true, true);
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
