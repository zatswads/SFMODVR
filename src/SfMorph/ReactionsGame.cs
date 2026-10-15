using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // The reflection bridge to the game's facial, voice, sound, sex-mode and material members for Reactions (0.7.9), and the dirty-talk
    // voice guard. Assembly-CSharp is never referenced: every type, field, property and method is looked up by name once per session
    // (log once per missing member, recorded in Missing, the feature that needs it turns off and nothing throws out of here).
    // Audio is reflected too: this project does not reference UnityEngine.AudioModule, so AudioSource/AudioClip are only ever held as
    // UnityEngine.Object / Behaviour and driven through cached PropertyInfo/MethodInfo (at event time or on the 0.1 s gate tick).
    //
    // Names follow the game survey (survey-game-reactions.md), which is authoritative. Where the design (design-0.7.9-reactions.md) differs:
    //   - FacialFunction.functionPause and LadyVoice_Function.functionPause both exist; the facial one is read, the voice one is the fallback.
    //   - "SexMode_System" is reached through FacialFunction.sexMode_System (fields sexMode, foreplayMode); the gauge is a separate
    //     object, SexMode_State_System (reikaDesire, ReikaDesirePlus(string)), found with FindObjectOfType every 2 s while missing.
    //   - Sources are Sound_Function.{moan,dirtyTalk,dialogue,soundEffect,voice}_audioSource; the "pause/playing" flags live on LadyVoice_Function.
    //   - Mode-1 moan clips are LadyVoice_Function.sexVoice_Source_Library[gameSetting_Function.voiceLanguage].Moan (AudioClip[]).
    //   - The caress breath uses PlayOneShot on the moan source (its clip/pitch stay untouched); it is not seen by the source's isPlaying,
    //     so OwnVoicePlaying tells when our own breath is still sounding.
    //   - Time: every "now" is whatever monotonic seconds Reactions passes (call ReadGates(now) every frame; it gates itself to 0.1 s).
    internal sealed class ReactionsGame
    {
        // ---- the 6.3 contract ----
        public bool Found;                 // the game's FacialFunction (and so the voice/face/sound objects) is bridged for this session
        public string Missing = "";        // comma separated list of game members that were not found (empty = all found)

        // ---- extra read-only state for Reactions.cs ----
        public bool HasMoan;               // MoanFunction was found, or the voice library is available for mode 1 (MoanClip())
        bool _hasMoanFn;                   // FacialFunction.MoanFunction itself exists (Moan() works)
        public bool HasLibrary;            // the voice library's Moan clips and the moan source exist (MoanClip() works); banks 0 short, 1 mid, 2 long
        public bool HasBreath;             // an Exhale clip array exists (Breath() works)
        public bool HasSfx;                // handJob_fast / sex_fast clips and the soundEffect source exist (Slap() works)
        public bool HasBlush;              // at least one of her materials has _BlushPower (SetBlush() works)
        public bool HasGaze;               // bipedIK.solvers.lookAt.IKPositionWeight found (SetGaze() works)
        public bool HasGauge;              // SexMode_State_System found (Desire is live, FeedDesire() works)
        public bool HasAnim;               // the facial Animator exists and has a "moan" trigger (WinceTrigger() works)
        public bool HasBlink;              // FacialFunction.Blink exists
        public int BlushFaceCount, BlushBodyCount;   // materials with _BlushPower, by the face/body split
        public float LastClipLength;       // seconds (at the played pitch) of the last voice clip Moan()/MoanClip()/Breath() started
        public UnityEngine.Object LastClip;       // that clip (for the event log: .name), null before the first
        public UnityEngine.Object LastSlapClip;   // the clip of the last Slap()
        public string BridgeLine = "";     // the one-per-session bridge log line (also logged once)
        public string GuardStatus = "off"; // the dirty-talk guard's status for the panel
        public int FeedCount;              // how many times FeedDesire() actually called the game
        public float BreathVolume = 0.5f;  // volume scale of Breath()
        // The gates, refreshed by ReadGates (plain fields, no reflection when read):
        public bool InSexMode;             // SexMode_System.sexMode || foreplayMode
        public bool VoiceOn;               // GameSetting_Function.voiceIsOn
        public bool SfxOn;                 // GameSetting_Function.soundEffectIsOn
        public bool BreakSystemOn;         // GameSetting_Function.dirtyTalkBreakSystemIsOn
        public bool DirtyTalkPlaying;      // LadyVoice_Function.dirtyTalkPlaying || dirtyTalk_audioSource.isPlaying
        public bool DialoguePlaying;       // dialoguePlaying || kissInteractionPlaying || sexFacialPlaying || dialogue_audioSource.isPlaying
        public bool DirtyTalkPause;        // LadyVoice_Function.dirtyTalkPause
        public bool FunctionPause;         // FacialFunction.functionPause
        public bool FacialOff;             // FacialFunction.facialLayer == "off"
        public string FacialLayer = "";    // FacialFunction.facialLayer ("on", "lip", "eye", "eyeBrow", "off")
        public bool MoanPlaying;           // moan_audioSource.isPlaying (the game's, or ours from MoanClip)
        public bool OwnVoicePlaying;       // our Breath() one-shot is still sounding (updated on every ReadGates call)
        public float TouchReactionTime;    // LadyBreastPhysics_Function.touchReactionTime (s)
        public float Desire = -1f;         // SexMode_State_System.reikaDesire (0..100); < 0 = unknown (no gauge)
        public int Language;               // GameSetting_Function.voiceLanguage

        const BindingFlags Own = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        const BindingFlags AnyM = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // ---- a field or property of a game object, found by name once ----
        sealed class Mem
        {
            public FieldInfo F; public PropertyInfo P;
            public bool Ok { get { return F != null || P != null; } }
            public Type Type { get { return F != null ? F.FieldType : P != null ? P.PropertyType : null; } }
            public object Get(object o)
            {
                if (o == null) return null;
                if (F != null) return F.GetValue(o);
                if (P != null) return P.GetValue(o, null);
                return null;
            }
            public void Set(object o, object v)
            {
                if (o == null) return;
                if (F != null) F.SetValue(o, v);
                else if (P != null && P.CanWrite) P.SetValue(o, v, null);
            }
        }

        // ---- a clip bank with the draw-and-park buffer of 4: a clip drawn is parked and not drawn again until 4 others were ----
        sealed class Bank
        {
            public UnityEngine.Object[] Clips;
            public readonly int[] Recent = { -1, -1, -1, -1 };
            public int Head;
            public int Count { get { return Clips != null ? Clips.Length : 0; } }
            public UnityEngine.Object Pick()
            {
                int n = Count;
                if (n == 0) return null;
                int hold = Mathf.Min(Recent.Length, n - 1), idx = -1;
                for (int tries = 0; tries < 12 && idx < 0; tries++)
                {
                    int c = UnityEngine.Random.Range(0, n);
                    if (!Parked(c, hold)) idx = c;
                }
                if (idx < 0) { for (int c = 0; c < n && idx < 0; c++) if (!Parked(c, hold)) idx = c; }
                if (idx < 0) idx = UnityEngine.Random.Range(0, n);
                Recent[Head] = idx; Head = (Head + 1) % Recent.Length;
                return Clips[idx];
            }
            bool Parked(int c, int hold)
            {
                // the last `hold` draws (newest first)
                for (int k = 1; k <= hold; k++) if (Recent[(Head - k + Recent.Length * 2) % Recent.Length] == c) return true;
                return false;
            }
        }

        sealed class BMat { public Material M; public bool Face; public float Base, Last; public bool Hold; }

        // ---- the bridged game objects (cached per session) ----
        Assembly _asm; bool _asmLooked;
        object _facial, _lv, _snd, _gs, _bp, _sm, _gauge, _solver;
        Animator _anim;
        Transform _herRoot;
        Behaviour _srcMoan, _srcDirty, _srcDialogue, _srcSfx, _srcVoice;
        Type _tSrc;
        // members
        Mem _mLv, _mSnd, _mBp, _mAnim, _mGs, _mSm, _mFPause, _mLayer, _mBiped;
        Mem _mDtPause, _mDialogue, _mKiss, _mSexFacial, _mDtPlaying, _mLvPause, _mLib, _mSfxLib;
        Mem _mDtActive, _mDtList, _mDtVoice, _mDtNum, _mRandSeq, _mDtSpeed, _mDtType;
        Mem _mSrcMoan, _mSrcDirty, _mSrcDialogue, _mSrcSfx, _mSrcVoice;
        Mem _mVoiceOn, _mSfxOn, _mBreakOn, _mLang;
        Mem _mSexMode, _mForeplay, _mTouchTime, _mDesire;
        MethodInfo _miMoan, _miBlink, _miDesirePlus;
        // audio members (AudioSource / AudioClip, reflected)
        PropertyInfo _pPlaying, _pClip, _pPitch, _pVolume, _pBlend, _pRolloff, _pMin, _pMax, _pPlayAwake, _pGroup, _pClipLen;
        MethodInfo _miPlay, _miStop, _miOneShot;
        Func<object, bool> _isPlayingFast;
        readonly object[] _argOneShot = new object[2];
        // gaze accessors
        Mem _mGazeW; Func<object, float> _gazeGet; Action<object, float> _gazeSet;
        // animator
        int _hMoan;
        // clips
        readonly Bank[] _moanBanks = { new Bank(), new Bank(), new Bank() };
        readonly Bank _breath = new Bank(), _slap = new Bank();
        int _bankLang = -1;
        // our sfx source
        GameObject _sfxGo; Behaviour _sfxSrc;
        // blush
        readonly List<BMat> _bm = new List<BMat>();
        readonly List<Renderer> _bmSrc = new List<Renderer>();
        int _idBlush;
        // trackers and timers
        bool _trigSet; float _trigT;
        bool _pitchHold; float _pitchBase, _pitchSet;
        bool _gHold; float _gBase, _gLast;
        float _now, _gateT = -100f, _nextFind, _nextSlow, _nextSet, _lastFeed = -100f, _ownEnd;
        int _moanFail; float _moanOffUntil;
        bool _bridgeLogged;
        readonly HashSet<string> _logged = new HashSet<string>();
        readonly List<string> _missing = new List<string>();

        // ---- guard state ----
        bool _guardSub, _guardDead, _guardBackoff;
        volatile bool _voiceErr;
        int _errAfter, _errTotal;
        bool _repaired; int _lastRepairMs, _guardMs, _repairLogMs, _backoffUntilMs, _repairs, _repairsSinceLog;
        bool _guardLoggedFirst;
        readonly Application.LogCallback _logCb;

        public ReactionsGame() { _logCb = OnLog; }

        void Log(string msg) { Plugin.Log.LogInfo("Reactions: " + msg); }
        void LogOnce(string key, string msg) { if (_logged.Add(key)) Plugin.Log.LogInfo("Reactions: " + msg); }

        void Miss(string label)
        {
            if (!_missing.Contains(label)) { _missing.Add(label); Missing = string.Join(", ", _missing.ToArray()); }
            LogOnce("miss:" + label, "game member missing: " + label + " (the feature that needs it is off)");
        }

        static bool Dead(object o)
        {
            var u = o as UnityEngine.Object;
            return !ReferenceEquals(u, null) && u == null;
        }

        static bool Due(ref float next, float now, float period)
        {
            if (now >= next || next - now > period + 0.5f) { next = now + period; return true; }
            return false;
        }

        Type Ty(string name)
        {
            if (!_asmLooked)
            {
                _asmLooked = true;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == "Assembly-CSharp") { _asm = a; break; }
                if (_asm == null) LogOnce("noasm", "Assembly-CSharp not found (not the game?)");
            }
            return _asm != null ? _asm.GetType(name) : null;
        }

        Mem M(Type t, string name, string label)
        {
            var m = new Mem();
            if (t != null)
                for (var k = t; k != null && !m.Ok && k != typeof(object); k = k.BaseType)
                {
                    m.F = k.GetField(name, Own);
                    if (m.F == null) { var p = k.GetProperty(name, Own); if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) m.P = p; }
                }
            if (!m.Ok) Miss(label);
            return m;
        }

        MethodInfo Me(Type t, string name, string label, params Type[] args)
        {
            MethodInfo mi = null;
            if (t != null)
                for (var k = t; k != null && mi == null && k != typeof(object); k = k.BaseType)
                    mi = k.GetMethod(name, AnyM | BindingFlags.DeclaredOnly, null, args, null);
            if (mi == null) Miss(label);
            return mi;
        }

        static bool Bool(Mem m, object o) { try { var v = m != null && m.Ok ? m.Get(o) : null; return v is bool && (bool)v; } catch (Exception) { return false; } }
        static int Int(Mem m, object o, int def) { try { var v = m != null && m.Ok ? m.Get(o) : null; return v == null ? def : Convert.ToInt32(v); } catch (Exception) { return def; } }
        static float Flt(Mem m, object o, float def) { try { var v = m != null && m.Ok ? m.Get(o) : null; return v == null ? def : Convert.ToSingle(v); } catch (Exception) { return def; } }

        // ======================================================================================================================
        // Refresh: find and cache the bridge (and re-check it on timers). Cheap when nothing is due: no search, no allocation.
        // ======================================================================================================================
        public bool Refresh(Character her, float now)
        {
            _now = now;
            try
            {
                if (her == null || her.Root == null) { if (Found) Drop("her gone"); return false; }
                if (Found && (Dead(_facial) || _herRoot != her.Root)) Drop(Dead(_facial) ? "the game's facial object is gone" : "her root changed");
                if (!Found)
                {
                    if (!Due(ref _nextFind, now, 2f)) return false;      // FindObjectOfType at most every 2 s while missing
                    object f = FindFacial();
                    if (f == null) { LogOnce("nofacial", "FacialFunction not found yet (looking again every 2 s)"); return false; }
                    _facial = f; _herRoot = her.Root;
                    Resolve(true);
                    DiscoverBlush(her);
                    FindGauge(now, true);
                    ResolveGaze();
                    ReadNow();
                    LogBridge();       // BridgeLine is ready before Found turns true (the director logs it once)
                    Found = true;
                    return true;
                }
                if (Due(ref _nextSet, now, 0.5f)) CheckBlushSet(her);
                if (Due(ref _nextSlow, now, 2f))
                {
                    if (Dead(_lv) || Dead(_snd) || Dead(_gs) || Dead(_anim) || Dead(_srcMoan) || (_srcSfx != null && Dead(_srcSfx)))
                    {
                        LogOnce("reresolve" + _nextSlow.ToString("0"), "a game object was replaced, looking its members up again");
                        ReleaseHeld();
                        Resolve(false);
                    }
                    if (_gauge == null || Dead(_gauge)) FindGauge(now, false);
                    ResolveGaze();
                    for (int i = 0; i < _bm.Count; i++) if (Dead(_bm[i].M)) { DiscoverBlush(her); break; }
                }
                return true;
            }
            catch (Exception e)
            {
                LogOnce("refresh:" + e.GetType().Name, "bridge refresh failed: " + e.Message);
                return Found;
            }
        }

        object FindFacial()
        {
            object f = null;
            var tMove = Ty("MovePlayerAndGirl");
            if (tMove != null)
            {
                var mv = UnityEngine.Object.FindObjectOfType(tMove);
                if (mv != null)
                {
                    var mf = new Mem();
                    for (var k = tMove; k != null && !mf.Ok && k != typeof(object); k = k.BaseType)
                    {
                        mf.F = k.GetField("facialFunction", Own);
                        if (mf.F == null) { var p = k.GetProperty("facialFunction", Own); if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) mf.P = p; }
                    }
                    try { f = mf.Get(mv); } catch (Exception) { f = null; }
                }
            }
            if (f == null || Dead(f))
            {
                var tF = Ty("FacialFunction");
                f = tF != null ? UnityEngine.Object.FindObjectOfType(tF) : null;
            }
            return Dead(f) ? null : f;
        }

        // Look every member up (once per session, or again when a game object was replaced).
        void Resolve(bool first)
        {
            if (first) { _missing.Clear(); Missing = ""; _bridgeLogged = false; }
            var tf = _facial.GetType();
            _mLv = M(tf, "ladyVoice_Function", "FacialFunction.ladyVoice_Function");
            _mSnd = M(tf, "sound_Function", "FacialFunction.sound_Function");
            _mBp = M(tf, "ladyBreastPhysics_Function", "FacialFunction.ladyBreastPhysics_Function");
            _mAnim = M(tf, "anim", "FacialFunction.anim");
            _mGs = M(tf, "gameSetting_Function", "FacialFunction.gameSetting_Function");
            _mSm = M(tf, "sexMode_System", "FacialFunction.sexMode_System");
            _mFPause = M(tf, "functionPause", "FacialFunction.functionPause");
            _mLayer = M(tf, "facialLayer", "FacialFunction.facialLayer");
            _mBiped = M(tf, "bipedIK", "FacialFunction.bipedIK");
            _miMoan = Me(tf, "MoanFunction", "FacialFunction.MoanFunction()", Type.EmptyTypes);
            _miBlink = Me(tf, "Blink", "FacialFunction.Blink()", Type.EmptyTypes);
            _hasMoanFn = _miMoan != null; HasMoan = _hasMoanFn; HasBlink = _miBlink != null;

            _lv = _mLv.Get(_facial); _snd = _mSnd.Get(_facial); _bp = _mBp.Get(_facial); _gs = _mGs.Get(_facial); _sm = _mSm.Get(_facial);
            _anim = _mAnim.Get(_facial) as Animator;
            if (Dead(_lv)) _lv = null;
            if (Dead(_snd)) _snd = null;

            // animator parameters: only fire a trigger the controller has (a missing one would warn every call)
            HasAnim = false;
            if (_anim != null)
            {
                _hMoan = Animator.StringToHash("moan");
                try
                {
                    foreach (var p in _anim.parameters)
                        if (p.type == AnimatorControllerParameterType.Trigger && p.nameHash == _hMoan) HasAnim = true;
                }
                catch (Exception e) { LogOnce("animparams", "Animator parameters unreadable: " + e.Message); HasAnim = true; }
                if (!HasAnim) Miss("Animator trigger 'moan'");
            }

            // LadyVoice_Function
            var tl = _lv != null ? _lv.GetType() : Ty("LadyVoice_Function");
            _mDtPause = M(tl, "dirtyTalkPause", "LadyVoice_Function.dirtyTalkPause");
            _mDialogue = M(tl, "dialoguePlaying", "LadyVoice_Function.dialoguePlaying");
            _mKiss = M(tl, "kissInteractionPlaying", "LadyVoice_Function.kissInteractionPlaying");
            _mSexFacial = M(tl, "sexFacialPlaying", "LadyVoice_Function.sexFacialPlaying");
            _mDtPlaying = M(tl, "dirtyTalkPlaying", "LadyVoice_Function.dirtyTalkPlaying");
            _mLvPause = M(tl, "functionPause", "LadyVoice_Function.functionPause");
            _mLib = M(tl, "sexVoice_Source_Library", "LadyVoice_Function.sexVoice_Source_Library");
            _mSfxLib = M(tl, "soundEffect_Library", "LadyVoice_Function.soundEffect_Library");
            _mDtActive = M(tl, "dirtyTalkActive", "LadyVoice_Function.dirtyTalkActive");
            _mDtList = M(tl, "dirtyTalkVoiceList", "LadyVoice_Function.dirtyTalkVoiceList");
            _mDtVoice = M(tl, "dirtyTalkVoice", "LadyVoice_Function.dirtyTalkVoice");
            _mDtNum = M(tl, "dirtyTalkPlayNum", "LadyVoice_Function.dirtyTalkPlayNum");
            _mRandSeq = M(tl, "randomSequence", "LadyVoice_Function.randomSequence");
            _mDtSpeed = M(tl, "dirtyTalkSpeedTime", "LadyVoice_Function.dirtyTalkSpeedTime");
            _mDtType = M(tl, "dirtyTalkType", "LadyVoice_Function.dirtyTalkType");

            // Sound_Function: the sources
            var ts = _snd != null ? _snd.GetType() : Ty("Sound_Function");
            _mSrcMoan = M(ts, "moan_audioSource", "Sound_Function.moan_audioSource");
            _mSrcDirty = M(ts, "dirtyTalk_audioSource", "Sound_Function.dirtyTalk_audioSource");
            _mSrcDialogue = M(ts, "dialogue_audioSource", "Sound_Function.dialogue_audioSource");
            _mSrcSfx = M(ts, "soundEffect_audioSource", "Sound_Function.soundEffect_audioSource");
            _mSrcVoice = M(ts, "voice_audioSource", "Sound_Function.voice_audioSource");
            _srcMoan = _mSrcMoan.Get(_snd) as Behaviour; _srcDirty = _mSrcDirty.Get(_snd) as Behaviour;
            _srcDialogue = _mSrcDialogue.Get(_snd) as Behaviour; _srcSfx = _mSrcSfx.Get(_snd) as Behaviour; _srcVoice = _mSrcVoice.Get(_snd) as Behaviour;
            _tSrc = null;
            foreach (var s in new[] { _srcMoan, _srcSfx, _srcVoice, _srcDirty, _srcDialogue }) if (s != null) { _tSrc = s.GetType(); break; }
            ResolveAudioMembers();

            // settings, sex mode, breast physics
            var tg = _gs != null ? _gs.GetType() : Ty("GameSetting_Function");
            _mVoiceOn = M(tg, "voiceIsOn", "GameSetting_Function.voiceIsOn");
            _mSfxOn = M(tg, "soundEffectIsOn", "GameSetting_Function.soundEffectIsOn");
            _mBreakOn = M(tg, "dirtyTalkBreakSystemIsOn", "GameSetting_Function.dirtyTalkBreakSystemIsOn");
            _mLang = M(tg, "voiceLanguage", "GameSetting_Function.voiceLanguage");
            var tm = _sm != null ? _sm.GetType() : Ty("SexMode_System");
            _mSexMode = M(tm, "sexMode", "SexMode_System.sexMode");
            _mForeplay = M(tm, "foreplayMode", "SexMode_System.foreplayMode");
            var tb = _bp != null ? _bp.GetType() : Ty("LadyBreastPhysics_Function");
            _mTouchTime = M(tb, "touchReactionTime", "LadyBreastPhysics_Function.touchReactionTime");

            BuildLibrary(Int(_mLang, _gs, 0));
            HasMoan = _hasMoanFn || HasLibrary;
            BuildSfx();
        }

        void ResolveAudioMembers()
        {
            _pPlaying = _pClip = _pPitch = _pVolume = _pBlend = _pRolloff = _pMin = _pMax = _pPlayAwake = _pGroup = _pClipLen = null;
            _miPlay = _miStop = _miOneShot = null; _isPlayingFast = null;
            if (_tSrc == null) { Miss("AudioSource (no source found)"); return; }
            _pPlaying = _tSrc.GetProperty("isPlaying");
            _pClip = _tSrc.GetProperty("clip");
            _pPitch = _tSrc.GetProperty("pitch");
            _pVolume = _tSrc.GetProperty("volume");
            _pBlend = _tSrc.GetProperty("spatialBlend");
            _pRolloff = _tSrc.GetProperty("rolloffMode");
            _pMin = _tSrc.GetProperty("minDistance");
            _pMax = _tSrc.GetProperty("maxDistance");
            _pPlayAwake = _tSrc.GetProperty("playOnAwake");
            _pGroup = _tSrc.GetProperty("outputAudioMixerGroup");
            _miPlay = _tSrc.GetMethod("Play", Type.EmptyTypes);
            _miStop = _tSrc.GetMethod("Stop", Type.EmptyTypes);
            foreach (var mi in _tSrc.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (mi.Name != "PlayOneShot") continue;
                var ps = mi.GetParameters();
                if (ps.Length == 2 && ps[1].ParameterType == typeof(float)) { _miOneShot = mi; break; }
            }
            if (_pPlaying == null) Miss("AudioSource.isPlaying");
            if (_pClip == null || _miPlay == null) Miss("AudioSource.clip/Play");
            if (_miOneShot == null) Miss("AudioSource.PlayOneShot(clip, volume)");
            if (_pPlaying != null)
            {
                try
                {
                    var dm = new DynamicMethod("sfr_isplaying", typeof(bool), new[] { typeof(object) }, typeof(ReactionsGame).Module, true);
                    var il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, _tSrc);
                    il.Emit(OpCodes.Callvirt, _pPlaying.GetGetMethod());
                    il.Emit(OpCodes.Ret);
                    _isPlayingFast = (Func<object, bool>)dm.CreateDelegate(typeof(Func<object, bool>));
                }
                catch (Exception) { _isPlayingFast = null; }
            }
            if (_pClip != null) _pClipLen = _pClip.PropertyType.GetProperty("length");
            if (_pClipLen == null && _pClip != null) Miss("AudioClip.length");
        }

        bool Playing(Behaviour src)
        {
            if (src == null || Dead(src) || _pPlaying == null) return false;
            try
            {
                if (_isPlayingFast != null) return _isPlayingFast(src);
                var v = _pPlaying.GetValue(src, null);
                return v is bool && (bool)v;
            }
            catch (Exception) { return false; }
        }

        float ClipLen(UnityEngine.Object clip)
        {
            if (clip == null || _pClipLen == null) return 0f;
            try { return Convert.ToSingle(_pClipLen.GetValue(clip, null)); } catch (Exception) { return 0f; }
        }

        // The language's Moan clips sorted by length into three banks, the Exhale and the SFX arrays. Allocates (once per language/session).
        void BuildLibrary(int lang)
        {
            _bankLang = lang;
            for (int i = 0; i < _moanBanks.Length; i++) { _moanBanks[i].Clips = null; Array.Clear(_moanBanks[i].Recent, 0, 4); for (int k = 0; k < 4; k++) _moanBanks[i].Recent[k] = -1; }
            _breath.Clips = null; for (int k = 0; k < 4; k++) _breath.Recent[k] = -1;
            HasLibrary = false; HasBreath = false;
            if (!_mLib.Ok || _lv == null || _srcMoan == null || _pClip == null || _miPlay == null) return;
            try
            {
                var libs = _mLib.Get(_lv) as Array;
                if (libs == null || lang < 0 || lang >= libs.Length) { LogOnce("liblang" + lang, "voice library for language " + lang + " not available (" + (libs == null ? "no array" : libs.Length + " entries") + ")"); return; }
                var lib = libs.GetValue(lang);
                if (lib == null) return;
                var tl = lib.GetType();
                var moan = M(tl, "Moan", "SexVoice_Source_Library.Moan").Get(lib) as Array;
                var clips = ToClips(moan);
                if (clips != null && clips.Length > 0)
                {
                    var keys = new float[clips.Length];
                    for (int i = 0; i < clips.Length; i++) keys[i] = ClipLen(clips[i]);
                    Array.Sort(keys, clips);
                    int n = clips.Length;
                    if (n < 3) { for (int b = 0; b < 3; b++) _moanBanks[b].Clips = clips; }
                    else
                    {
                        int a = n / 3, c = 2 * n / 3;
                        _moanBanks[0].Clips = Slice(clips, 0, a); _moanBanks[1].Clips = Slice(clips, a, c); _moanBanks[2].Clips = Slice(clips, c, n);
                    }
                    HasLibrary = true;
                }
                var ex = ToClips(M(tl, "Exhale_slow", "SexVoice_Source_Library.Exhale_slow").Get(lib) as Array);
                if (ex == null || ex.Length == 0) ex = ToClips(M(tl, "Exhale_normal", "SexVoice_Source_Library.Exhale_normal").Get(lib) as Array);
                if (ex != null && ex.Length > 0 && _miOneShot != null) { _breath.Clips = ex; HasBreath = true; }
            }
            catch (Exception e) { LogOnce("buildlib", "voice library unreadable: " + e.Message); HasLibrary = false; }
            if (_bridgeLogged) Log("voice language " + lang + ": " + BanksText());
        }

        static UnityEngine.Object[] ToClips(Array a)
        {
            if (a == null) return null;
            var l = new List<UnityEngine.Object>(a.Length);
            foreach (var o in a) { var u = o as UnityEngine.Object; if (u != null) l.Add(u); }
            return l.ToArray();
        }

        static UnityEngine.Object[] Slice(UnityEngine.Object[] a, int from, int to)
        {
            var r = new UnityEngine.Object[to - from];
            Array.Copy(a, from, r, 0, r.Length);
            return r;
        }

        // The SFX clip arrays (handJob_fast, else sex_fast) of SoundEffect_Library, not per language.
        void BuildSfx()
        {
            _slap.Clips = null; for (int k = 0; k < 4; k++) _slap.Recent[k] = -1;
            HasSfx = false;
            if (!_mSfxLib.Ok || _lv == null || _srcSfx == null || _miOneShot == null) { return; }
            try
            {
                var lib = _mSfxLib.Get(_lv);
                if (lib == null) { Miss("LadyVoice_Function.soundEffect_Library (null)"); return; }
                var tl = lib.GetType();
                var clips = ToClips(M(tl, "handJob_fast", "SoundEffect_Library.handJob_fast").Get(lib) as Array);
                if (clips == null || clips.Length == 0) clips = ToClips(M(tl, "sex_fast", "SoundEffect_Library.sex_fast").Get(lib) as Array);
                if (clips != null && clips.Length > 0) { _slap.Clips = clips; HasSfx = true; }
            }
            catch (Exception e) { LogOnce("buildsfx", "sound effect library unreadable: " + e.Message); }
        }

        void FindGauge(float now, bool first)
        {
            var t = Ty("SexMode_State_System");
            if (t == null) { if (first) { Miss("SexMode_State_System (type)"); } HasGauge = false; return; }
            _gauge = UnityEngine.Object.FindObjectOfType(t);
            if (_gauge == null) { if (first) LogOnce("nogauge", "desire gauge (SexMode_State_System) not in the scene yet (looking again every 2 s)"); HasGauge = false; return; }
            _mDesire = M(t, "reikaDesire", "SexMode_State_System.reikaDesire");
            if (_miDesirePlus == null) _miDesirePlus = Me(t, "ReikaDesirePlus", "SexMode_State_System.ReikaDesirePlus(string)", typeof(string));
            HasGauge = _mDesire.Ok;
            if (_bridgeLogged && HasGauge) LogOnce("gaugefound", "desire gauge found: reikaDesire " + Desire.ToString("0"));
        }

        void ResolveGaze()
        {
            try
            {
                var biped = _mBiped != null && _mBiped.Ok ? _mBiped.Get(_facial) : null;
                if (biped == null || Dead(biped)) { _solver = null; HasGaze = false; return; }
                var solvers = M(biped.GetType(), "solvers", "BipedIK.solvers").Get(biped);
                var look = solvers != null ? M(solvers.GetType(), "lookAt", "BipedIK.solvers.lookAt").Get(solvers) : null;
                if (look == null) { _solver = null; HasGaze = false; return; }
                if (ReferenceEquals(look, _solver) && _mGazeW != null && _mGazeW.Ok) return;
                _solver = look;
                _mGazeW = M(look.GetType(), "IKPositionWeight", "IKSolverLookAt.IKPositionWeight");
                _gazeGet = null; _gazeSet = null;
                if (_mGazeW.F != null && _mGazeW.F.FieldType == typeof(float))
                {
                    try
                    {
                        var g = new DynamicMethod("sfr_gz_get", typeof(float), new[] { typeof(object) }, typeof(ReactionsGame).Module, true);
                        var il = g.GetILGenerator();
                        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, _mGazeW.F.DeclaringType); il.Emit(OpCodes.Ldfld, _mGazeW.F); il.Emit(OpCodes.Ret);
                        _gazeGet = (Func<object, float>)g.CreateDelegate(typeof(Func<object, float>));
                        var s = new DynamicMethod("sfr_gz_set", typeof(void), new[] { typeof(object), typeof(float) }, typeof(ReactionsGame).Module, true);
                        il = s.GetILGenerator();
                        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, _mGazeW.F.DeclaringType); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, _mGazeW.F); il.Emit(OpCodes.Ret);
                        _gazeSet = (Action<object, float>)s.CreateDelegate(typeof(Action<object, float>));
                    }
                    catch (Exception) { _gazeGet = null; _gazeSet = null; }
                }
                HasGaze = _mGazeW.Ok;
            }
            catch (Exception e) { LogOnce("gaze:" + e.GetType().Name, "look-at solver unreadable: " + e.Message); HasGaze = false; }
        }

        // ---- blush materials: every shared material of her renderers with _BlushPower ----
        void CheckBlushSet(Character her)
        {
            bool same = _bmSrc.Count == her.Renderers.Count;
            for (int i = 0; same && i < _bmSrc.Count; i++) if (!ReferenceEquals(_bmSrc[i], her.Renderers[i])) same = false;
            if (!same) DiscoverBlush(her);
        }

        void DiscoverBlush(Character her)
        {
            RestoreBlush();
            _bm.Clear(); _bmSrc.Clear();
            BlushFaceCount = BlushBodyCount = 0;
            if (_idBlush == 0) _idBlush = Shader.PropertyToID("_BlushPower");
            try
            {
                foreach (var r in her.Renderers)
                {
                    if (r == null) continue;
                    _bmSrc.Add(r);
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null || !m.HasProperty(_idBlush)) continue;
                        bool dup = false;
                        for (int k = 0; k < _bm.Count; k++) if (_bm[k].M == m) { dup = true; break; }
                        if (dup) continue;
                        string rn = r.name.ToLowerInvariant(), mn = m.name.ToLowerInvariant();
                        bool face = rn.Contains("face") || rn.Contains("head") || mn.Contains("face") || mn.Contains("head");
                        var bm = new BMat { M = m, Face = face, Base = m.GetFloat(_idBlush) };
                        bm.Last = bm.Base;
                        _bm.Add(bm);
                        if (face) BlushFaceCount++; else BlushBodyCount++;
                    }
                }
            }
            catch (Exception e) { LogOnce("blushscan", "blush materials unreadable: " + e.Message); }
            HasBlush = _bm.Count > 0;
            if (_bridgeLogged) Log("blush materials re-read: " + BlushText());
        }

        string BlushText()
        {
            if (_bm.Count == 0) return "none with _BlushPower";
            var sb = new StringBuilder();
            for (int i = 0; i < _bm.Count; i++)
            {
                var b = _bm[i];
                if (i > 0) sb.Append("; ");
                string sh = "?";
                try { sh = b.M.shader != null ? b.M.shader.name : "?"; } catch (Exception) { }
                sb.Append(b.Face ? "face " : "body ").Append(b.M.name).Append(" base ").Append(b.Base.ToString("0.###")).Append(" [").Append(sh).Append(']');
            }
            return sb.ToString();
        }

        string BanksText()
        {
            var sb = new StringBuilder();
            string[] nm = { "short", "mid", "long" };
            for (int b = 0; b < 3; b++)
            {
                if (b > 0) sb.Append(" | ");
                sb.Append(nm[b]).Append(": ");
                var cl = _moanBanks[b].Clips;
                if (cl == null || cl.Length == 0) { sb.Append("-"); continue; }
                for (int i = 0; i < cl.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(cl[i].name).Append(' ').Append(ClipLen(cl[i]).ToString("0.00")).Append('s');
                }
            }
            return sb.ToString();
        }

        void LogBridge()
        {
            if (_bridgeLogged) return;
            _bridgeLogged = true;
            var sb = new StringBuilder("game bridge: FacialFunction");
            sb.Append(_lv != null ? ", LadyVoice" : "").Append(_snd != null ? ", Sound" : "").Append(_gs != null ? ", GameSetting" : "")
              .Append(_sm != null ? ", SexMode" : "").Append(_anim != null ? ", Animator" + (HasAnim ? "(moan)" : "(no moan trigger)") : "");
            sb.Append("; sources: moan ").Append(_srcMoan != null ? "y" : "n").Append(", dirtyTalk ").Append(_srcDirty != null ? "y" : "n")
              .Append(", dialogue ").Append(_srcDialogue != null ? "y" : "n").Append(", sfx ").Append(_srcSfx != null ? "y" : "n").Append(", voice ").Append(_srcVoice != null ? "y" : "n");
            string mixer = "?";
            try { var g = _pGroup != null && _srcSfx != null ? _pGroup.GetValue(_srcSfx, null) as UnityEngine.Object : null; mixer = g != null ? g.name : "none"; } catch (Exception) { }
            sb.Append("; sfx mixer group ").Append(mixer);
            sb.Append("; MoanFunction ").Append(HasMoan ? "y" : "n").Append(", Blink ").Append(HasBlink ? "y" : "n");
            sb.Append("; gauge ").Append(HasGauge ? "reikaDesire " + Desire.ToString("0") : "none");
            sb.Append("; touchReactionTime ").Append(TouchReactionTime.ToString("0.##")).Append("; language ").Append(Language);
            sb.Append("; moan banks [").Append(HasLibrary ? BanksText() : "none").Append("]");
            sb.Append("; breath clips ").Append(_breath.Count);
            sb.Append("; sfx clips ").Append(_slap.Count).Append(" (handJob_fast, else sex_fast)");
            sb.Append("; blush ").Append(BlushText());
            sb.Append("; look-at ").Append(HasGaze ? "found" : "none");
            sb.Append("; missing: ").Append(Missing.Length == 0 ? "none" : Missing);
            BridgeLine = sb.ToString();   // not logged here: Reactions logs it once as "Reactions: " + BridgeLine
        }

        // ======================================================================================================================
        // Gates
        // ======================================================================================================================
        // Call every frame; reads the game's flags into the public fields at most every 0.1 s, and runs the "moan" trigger safety.
        public void ReadGates(float now)
        {
            _now = now;
            TickTrigger();
            OwnVoicePlaying = now < _ownEnd;
            if (!Found) return;
            if (now - _gateT < 0.1f && now >= _gateT) return;
            _gateT = now;
            ReadNow();
        }

        // The same, immediately (for event time, just before an arbiter decision).
        public void ReadGatesNow(float now)
        {
            _now = now;
            OwnVoicePlaying = now < _ownEnd;
            if (!Found) return;
            _gateT = now;
            ReadNow();
        }

        void ReadNow()
        {
            try
            {
                if (Dead(_facial)) return;
                InSexMode = _sm != null && (Bool(_mSexMode, _sm) || Bool(_mForeplay, _sm));
                VoiceOn = _gs == null || Bool(_mVoiceOn, _gs);
                SfxOn = _gs == null || Bool(_mSfxOn, _gs);
                BreakSystemOn = Bool(_mBreakOn, _gs);
                int lang = Int(_mLang, _gs, 0);
                Language = lang;
                if (lang != _bankLang) { BuildLibrary(lang); HasMoan = _hasMoanFn || HasLibrary; }
                DirtyTalkPause = Bool(_mDtPause, _lv);
                DirtyTalkPlaying = Bool(_mDtPlaying, _lv) || Playing(_srcDirty);
                DialoguePlaying = Bool(_mDialogue, _lv) || Bool(_mKiss, _lv) || Bool(_mSexFacial, _lv) || Playing(_srcDialogue);
                FunctionPause = _mFPause.Ok ? Bool(_mFPause, _facial) : Bool(_mLvPause, _lv);
                var layer = _mLayer.Ok ? _mLayer.Get(_facial) as string : null;
                FacialLayer = layer ?? "";
                FacialOff = layer != null && layer == "off";
                MoanPlaying = Playing(_srcMoan);
                TouchReactionTime = Flt(_mTouchTime, _bp, 0f);
                Desire = HasGauge && _gauge != null && !Dead(_gauge) ? Flt(_mDesire, _gauge, -1f) : -1f;
                PitchTick();
            }
            catch (Exception e) { LogOnce("gates:" + e.GetType().Name, "reading the game's gates failed: " + e.Message); }
        }

        // ======================================================================================================================
        // Voice and face
        // ======================================================================================================================
        void MarkTrigger() { _trigSet = true; _trigT = _now; }

        void TickTrigger()
        {
            if (_trigSet && (_now - _trigT >= 1f || _now < _trigT)) { _trigSet = false; ResetTriggerNow(); }
        }

        void ResetTriggerNow()
        {
            try { if (_anim != null && !Dead(_anim) && HasAnim) _anim.ResetTrigger(_hMoan); }
            catch (Exception e) { LogOnce("resettrig", "ResetTrigger failed: " + e.Message); }
        }

        // The "moan" trigger safety: 1 s after any trigger we (or the game's MoanFunction) set, reset it. ReadGates calls it too.
        public void ResetMoan() { TickTrigger(); }

        // The game's own touch reaction: MoanFunction() (Animator "moan" + a Moan clip + the dirty-talk pause). The arbiter is Reactions'.
        public bool Moan()
        {
            if (!Found || !_hasMoanFn || Dead(_facial)) return false;
            if (_now < _moanOffUntil) return false;
            try
            {
                RestorePitch();
                _miMoan.Invoke(_facial, null);
                _moanFail = 0;
                MarkTrigger();
                if (_srcMoan != null && _pClip != null)
                {
                    LastClip = _pClip.GetValue(_srcMoan, null) as UnityEngine.Object;
                    float len = ClipLen(LastClip);
                    if (len > 0f) LastClipLength = len / Mathf.Max(0.5f, GetPitch(_srcMoan, 1f));
                }
                return true;
            }
            catch (Exception e)
            {
                var ie = e.InnerException ?? e;
                LogOnce("moanerr:" + ie.GetType().Name, "MoanFunction threw " + ie.GetType().Name + ": " + ie.Message);
                if (++_moanFail >= 5) { _moanOffUntil = _now + 30f; _moanFail = 0; LogOnce("moanoff" + _now.ToString("0"), "MoanFunction failed 5 times in a row, paused for 30 s"); }
                return false;
            }
        }

        // Mode 1: our own pick from bank 0 short / 1 mid / 2 long (draw-and-park 4) on the moan source at `pitch` (AudioSource.pitch, 1 = as
        // recorded), voice loop stopped, "moan" trigger for the mouth. The pitch is restored when the clip ends (ReadGates tick).
        public bool MoanClip(int bank, float pitch)
        {
            if (!Found || !HasLibrary || _srcMoan == null || Dead(_srcMoan)) return false;
            try
            {
                var b = _moanBanks[Mathf.Clamp(bank, 0, _moanBanks.Length - 1)];
                var clip = b.Pick();
                if (clip == null) return false;
                if (!_pitchHold) _pitchBase = GetPitch(_srcMoan, 1f);
                _pClip.SetValue(_srcMoan, clip, null);
                if (_pPitch != null) _pPitch.SetValue(_srcMoan, pitch, null);
                _pitchHold = true; _pitchSet = pitch;
                _miPlay.Invoke(_srcMoan, null);
                if (_srcVoice != null && !Dead(_srcVoice) && _miStop != null) _miStop.Invoke(_srcVoice, null);
                LastClip = clip; LastClipLength = ClipLen(clip) / Mathf.Max(0.5f, pitch);
                if (HasAnim) { _anim.SetTrigger(_hMoan); MarkTrigger(); }
                return true;
            }
            catch (Exception e) { LogOnce("moanclip:" + e.GetType().Name, "own moan clip failed: " + e.Message); return false; }
        }

        float GetPitch(Behaviour src, float def)
        {
            try { return _pPitch != null ? Convert.ToSingle(_pPitch.GetValue(src, null)) : def; } catch (Exception) { return def; }
        }

        void RestorePitch()
        {
            if (!_pitchHold) return;
            _pitchHold = false;
            try
            {
                if (_srcMoan != null && !Dead(_srcMoan) && _pPitch != null && Mathf.Abs(GetPitch(_srcMoan, _pitchSet) - _pitchSet) < 1e-3f) _pPitch.SetValue(_srcMoan, _pitchBase, null);
            }
            catch (Exception) { }
        }

        void PitchTick()
        {
            if (_pitchHold && !MoanPlaying) RestorePitch();   // MoanPlaying was just read: the clip ended
        }

        // A quiet breath (Exhale_slow, else Exhale_normal) as a one-shot on the moan source; no trigger. Skipped while the game's moan sounds.
        public bool Breath()
        {
            if (!Found || !HasBreath || _srcMoan == null || Dead(_srcMoan)) return false;
            try
            {
                if (Playing(_srcMoan)) return false;
                RestorePitch();
                var clip = _breath.Pick();
                if (clip == null) return false;
                _argOneShot[0] = clip; _argOneShot[1] = BreathVolume;
                _miOneShot.Invoke(_srcMoan, _argOneShot);
                LastClip = clip; LastClipLength = ClipLen(clip);
                _ownEnd = _now + LastClipLength;
                OwnVoicePlaying = true;
                return true;
            }
            catch (Exception e) { LogOnce("breath:" + e.GetType().Name, "breath failed: " + e.Message); return false; }
        }

        // Experimental (panel): write one of the game's Animator floats once (mood_num, reaction_num). What they mean is unknown, so the
        // first call logs the controller's parameter list; a float the controller lacks is refused instead of warning every call.
        bool _paramsLogged;
        public bool SetAnimFloat(string name, float v)
        {
            if (!Found || _anim == null || Dead(_anim) || string.IsNullOrEmpty(name)) return false;
            try
            {
                int h = Animator.StringToHash(name);
                bool has = false;
                var sb = _paramsLogged ? null : new System.Text.StringBuilder();
                foreach (var p in _anim.parameters)
                {
                    if (p.nameHash == h && p.type == AnimatorControllerParameterType.Float) has = true;
                    if (sb != null) sb.Append(sb.Length > 0 ? ", " : "").Append(p.name).Append(':').Append(p.type);
                }
                if (sb != null) { _paramsLogged = true; Plugin.Log.LogInfo("Reactions: her Animator parameters: " + sb); }
                if (!has) { LogOnce("nofloat:" + name, "her Animator has no float '" + name + "'"); return false; }
                float was = _anim.GetFloat(h);
                _anim.SetFloat(h, v);
                Plugin.Log.LogInfo("Reactions: set Animator " + name + " " + was.ToString("0.##") + " -> " + v.ToString("0.##") + " (experimental; the game may overwrite it)");
                return true;
            }
            catch (Exception e) { LogOnce("setfloat:" + e.GetType().Name, "Animator float failed: " + e.Message); return false; }
        }

        // The face without a voice: the Animator's "moan" trigger (a wince). Never layer weights, never "eyelid".
        public bool WinceTrigger()
        {
            if (!Found || !HasAnim || _anim == null || Dead(_anim)) return false;
            try { _anim.SetTrigger(_hMoan); MarkTrigger(); return true; }
            catch (Exception e) { LogOnce("wince:" + e.GetType().Name, "moan trigger failed: " + e.Message); return false; }
        }

        public bool Blink()
        {
            if (!Found || !HasBlink || Dead(_facial)) return false;
            try { _miBlink.Invoke(_facial, null); return true; }
            catch (Exception e) { LogOnce("blink:" + e.GetType().Name, "Blink failed: " + (e.InnerException ?? e).Message); return false; }
        }

        // ======================================================================================================================
        // Slap sound: our own 3D source under her root on the game's sound-effect mixer group
        // ======================================================================================================================
        bool EnsureSfx()
        {
            if (_sfxSrc != null && !Dead(_sfxSrc)) return true;
            if (_tSrc == null || _herRoot == null || _srcSfx == null) return false;
            try
            {
                _sfxGo = new GameObject("SfReact_sfx");
                _sfxGo.transform.SetParent(_herRoot, false);
                _sfxSrc = _sfxGo.AddComponent(_tSrc) as Behaviour;
                if (_sfxSrc == null) { UnityEngine.Object.Destroy(_sfxGo); _sfxGo = null; return false; }
                if (_pPlayAwake != null) _pPlayAwake.SetValue(_sfxSrc, false, null);
                if (_pBlend != null) _pBlend.SetValue(_sfxSrc, 1f, null);
                if (_pRolloff != null) _pRolloff.SetValue(_sfxSrc, Enum.ToObject(_pRolloff.PropertyType, 0), null);   // Logarithmic
                if (_pMin != null) _pMin.SetValue(_sfxSrc, 0.3f, null);
                if (_pMax != null) _pMax.SetValue(_sfxSrc, 8f, null);
                if (_pGroup != null) _pGroup.SetValue(_sfxSrc, _pGroup.GetValue(_srcSfx, null), null);
                return true;
            }
            catch (Exception e) { LogOnce("sfxsrc:" + e.GetType().Name, "own sound source failed: " + e.Message); _sfxSrc = null; return false; }
        }

        // A slap clip (handJob_fast, else sex_fast; draw-and-park 4) as a one-shot at `at` with volume scale `vol` and source pitch `pitch`.
        // Respects the game's sound-effect setting. Never touches the game's shared source or clip.
        public bool Slap(Vector3 at, float vol, float pitch)
        {
            if (!Found || !HasSfx || !SfxOn) return false;
            if (!EnsureSfx()) return false;
            try
            {
                var clip = _slap.Pick();
                if (clip == null) return false;
                _sfxGo.transform.position = at;
                if (_pPitch != null) _pPitch.SetValue(_sfxSrc, pitch, null);
                _argOneShot[0] = clip; _argOneShot[1] = Mathf.Max(0f, vol);
                _miOneShot.Invoke(_sfxSrc, _argOneShot);
                LastSlapClip = clip;
                return true;
            }
            catch (Exception e) { LogOnce("slap:" + e.GetType().Name, "slap sound failed: " + e.Message); return false; }
        }

        // ======================================================================================================================
        // Blush, gaze, desire
        // ======================================================================================================================
        // `face` / `body` = how much _BlushPower to ADD on top of the game's own value on the face / body materials (0 = give the game's value
        // back). Called every frame while a reaction wants it; writes only on a change > 0.002. Result is kept <= max(1, game's value).
        public void SetBlush(float face, float body)
        {
            if (!Found || _bm.Count == 0) return;
            try
            {
                for (int i = 0; i < _bm.Count; i++)
                {
                    var b = _bm[i];
                    if (b.M == null) continue;   // destroyed; rediscovered on the next 2 s check
                    float add = b.Face ? face : body;
                    float cur = b.M.GetFloat(_idBlush);
                    bool gameWrote = b.Hold && Mathf.Abs(cur - b.Last) > 1e-4f;
                    if (gameWrote) { b.Base = cur; b.Hold = false; }
                    else if (!b.Hold) b.Base = cur;
                    if (add <= 0.0005f)
                    {
                        if (b.Hold) { b.M.SetFloat(_idBlush, b.Base); b.Hold = false; b.Last = b.Base; }
                        continue;
                    }
                    float tgt = Mathf.Min(Mathf.Max(b.Base, 1f), b.Base + add);
                    if (b.Hold ? Mathf.Abs(tgt - b.Last) > 0.002f : Mathf.Abs(tgt - cur) > 0.002f)
                    {
                        b.M.SetFloat(_idBlush, tgt); b.Last = tgt; b.Hold = true;
                    }
                }
            }
            catch (Exception e) { LogOnce("blush:" + e.GetType().Name, "blush write failed: " + e.Message); }
        }

        void RestoreBlush()
        {
            for (int i = 0; i < _bm.Count; i++)
            {
                var b = _bm[i];
                try
                {
                    if (b.Hold && b.M != null && Mathf.Abs(b.M.GetFloat(_idBlush) - b.Last) <= 1e-4f) b.M.SetFloat(_idBlush, b.Base);
                }
                catch (Exception) { }
                b.Hold = false;
            }
        }

        // 0 = the game's look-at weight, 1 = looking away (weight 0). Scales bipedIK.solvers.lookAt.IKPositionWeight by (1 - away); weight only.
        // Called every frame (after the game's own write, before FinalIK solves); gives the game's value back when away is 0.
        public void SetGaze(float away)
        {
            if (!Found || !HasGaze || _solver == null) return;
            try
            {
                float cur = _gazeGet != null ? _gazeGet(_solver) : Flt(_mGazeW, _solver, 0f);
                bool gameWrote = _gHold && Mathf.Abs(cur - _gLast) > 1e-4f;
                if (gameWrote) _gBase = cur;
                else if (!_gHold) _gBase = cur;
                if (away > 0.001f)
                {
                    float w = _gBase * (1f - Mathf.Clamp01(away));
                    if (!_gHold || gameWrote || Mathf.Abs(w - cur) > 1e-4f) WriteGaze(w);
                    _gLast = w; _gHold = true;
                }
                else if (_gHold)
                {
                    if (!gameWrote) WriteGaze(_gBase);
                    _gHold = false;
                }
            }
            catch (Exception e) { LogOnce("gazew:" + e.GetType().Name, "look-at weight write failed: " + e.Message); HasGaze = false; }
        }

        void WriteGaze(float w)
        {
            if (_gazeSet != null) _gazeSet(_solver, w);
            else _mGazeW.Set(_solver, w);
        }

        void RestoreGaze()
        {
            if (!_gHold) return;
            _gHold = false;
            try
            {
                if (_solver == null || _mGazeW == null || !_mGazeW.Ok) return;
                float cur = _gazeGet != null ? _gazeGet(_solver) : Flt(_mGazeW, _solver, 0f);
                if (Mathf.Abs(cur - _gLast) <= 1e-4f) WriteGaze(_gBase);
            }
            catch (Exception) { }
        }

        // The game's desire gauge: ReikaDesirePlus("plus"), at most every 3 s, only in sex/foreplay mode. Never writes reikaDesire.
        public void FeedDesire()
        {
            if (!Found || !HasGauge || _miDesirePlus == null || _gauge == null || Dead(_gauge)) return;
            if (!InSexMode) return;
            if (_now - _lastFeed < 3f && _now >= _lastFeed) return;
            _lastFeed = _now;
            try { _miDesirePlus.Invoke(_gauge, new object[] { "plus" }); FeedCount++; }
            catch (Exception e) { LogOnce("feed:" + e.GetType().Name, "ReikaDesirePlus failed: " + (e.InnerException ?? e).Message); }
        }

        // ======================================================================================================================
        // The dirty-talk guard (LadyVoice_Function.PlayRandomVoice IndexOutOfRange every frame): keep dirtyTalkVoice / dirtyTalkPlayNum
        // valid for the current dirtyTalkVoiceList. Independent of the reactions toggle; never writes randomSequence or dirtyTalkActive.
        // ======================================================================================================================
        void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception || condition == null || stackTrace == null) return;
            if (condition.IndexOf("IndexOutOfRange", StringComparison.Ordinal) < 0) return;
            if (stackTrace.IndexOf("PlayRandomVoice", StringComparison.Ordinal) < 0) return;
            _voiceErr = true;
            System.Threading.Interlocked.Increment(ref _errAfter);
            _errTotal++;
        }

        static int Ms() { return Environment.TickCount; }

        public void Guard(bool on, float now)
        {
            _now = now;
            if (!on)
            {
                if (_guardSub) { try { Application.logMessageReceived -= _logCb; } catch (Exception) { } _guardSub = false; }
                GuardStatus = "off";
                return;
            }
            if (!_guardSub) { try { Application.logMessageReceived += _logCb; _guardSub = true; } catch (Exception e) { LogOnce("guardsub", "voice guard: log hook failed: " + e.Message); } }
            if (!Found) { GuardStatus = "waiting for the game"; return; }
            if (_guardDead) return;
            try
            {
                int ms = Ms();
                if (_guardBackoff)
                {
                    if (unchecked(ms - _backoffUntilMs) < 0) return;
                    _guardBackoff = false; _repaired = false; Interlocked0();
                    GuardStatus = "watching";
                }
                if (_repaired && System.Threading.Volatile.Read(ref _errAfter) > 20 && unchecked(ms - _lastRepairMs) < 2000)
                {
                    _guardBackoff = true; _backoffUntilMs = unchecked(ms + 60000);
                    GuardStatus = "repairs don't stop the error, left alone for 60 s";
                    Log("voice guard: repairs don't stop the error (" + _errAfter + " exceptions within 2 s of a repair), leaving it for 60 s. " + GuardDump("state"));
                    return;
                }
                bool err = _voiceErr;
                if (!err && unchecked(ms - _guardMs) < 200) return;
                _guardMs = ms; _voiceErr = false;
                GuardTick(err, ms);
            }
            catch (Exception e) { LogOnce("guarderr:" + e.GetType().Name, "voice guard failed: " + e.Message); }
        }

        void Interlocked0() { System.Threading.Interlocked.Exchange(ref _errAfter, 0); }

        void GuardTick(bool err, int ms)
        {
            var lv = _lv;
            if (lv == null || Dead(lv) || !_mDtActive.Ok || !_mDtList.Ok || !_mDtVoice.Ok || !_mDtNum.Ok || !_mRandSeq.Ok)
            {
                _guardDead = true; GuardStatus = "off: the game's dirty-talk members were not found";
                LogOnce("guarddead", "voice guard: off (dirty-talk members missing: " + Missing + ")");
                return;
            }
            int R = Int(_mRandSeq, lv, 0);
            if (R < 1 || R > 64)
            {
                _guardDead = true; GuardStatus = "off: randomSequence is " + R;
                LogOnce("guardR", "voice guard: off (randomSequence " + R + " is outside 1..64)");
                return;
            }
            if (!Bool(_mDtActive, lv)) { GuardStatus = _repairs == 0 ? "watching (dirty talk idle)" : "watching, repaired " + _repairs + " times"; return; }
            var list = _mDtList.Get(lv) as Array;
            int len = list != null ? list.Length : 0;
            int n = Int(_mDtNum, lv, -1);
            var seq = _mDtVoice.Get(lv) as int[];
            if (len == 0)
            {
                // an empty list throws in the shuffle every time the timer fires: never let it fire
                if (_mDtSpeed.Ok && Flt(_mDtSpeed, lv, 3f) < 1f) _mDtSpeed.Set(lv, 3f);
                LogOnce("guardempty", "voice guard: the dirty-talk list is empty while active, holding its timer (type " + (_mDtType.Get(lv) as string) + ")");
                GuardStatus = "dirty-talk list empty, timer held";
                return;
            }
            string why = null;
            if (n >= 0 && (seq == null || n >= seq.Length || seq[n] < 0 || seq[n] >= len)) why = "stale shuffle (index " + n + (seq != null && n < seq.Length ? " -> " + seq[n] : "") + " of a list of " + len + ")";
            else if (n < 0 && len < R) why = "list of " + len + " is shorter than the shuffle window " + R;
            if (why == null)
            {
                GuardStatus = _repairs == 0 ? "watching" : "watching, repaired " + _repairs + " times";
                if (err) LogOnce("guardvalid", "voice guard: PlayRandomVoice threw but the state looks valid. " + GuardDump("state"));
                return;
            }
            // rebuild: a Fisher-Yates permutation of the list's indices, reshuffled at every wrap, R entries, counter at the top
            var perm = new int[len];
            for (int i = 0; i < len; i++) perm[i] = i;
            var s = new int[R];
            int k = len;
            for (int i = 0; i < R; i++)
            {
                if (k >= len) { for (int j = len - 1; j > 0; j--) { int r = UnityEngine.Random.Range(0, j + 1); int t = perm[j]; perm[j] = perm[r]; perm[r] = t; } k = 0; }
                s[i] = perm[k++];
            }
            string dump = _repairs == 0 && !_guardLoggedFirst ? GuardDump(why) : null;
            _mDtVoice.Set(lv, s);
            _mDtNum.Set(lv, R - 1);
            _repairs++; _repairsSinceLog++; _repaired = true; _lastRepairMs = ms;
            System.Threading.Interlocked.Exchange(ref _errAfter, 0);
            GuardStatus = "repaired " + _repairs + " times (last: " + why + ")";
            if (dump != null) { _guardLoggedFirst = true; _repairLogMs = ms; _repairsSinceLog = 0; Log("voice guard: " + why + ", rebuilt the dirty-talk shuffle. " + dump); }
            else if (unchecked(ms - _repairLogMs) >= 10000) { _repairLogMs = ms; Log("voice guard: " + _repairsSinceLog + " more repair(s) in the last 10 s (last: " + why + "), " + _errTotal + " exceptions seen in all"); _repairsSinceLog = 0; }
        }

        string GuardDump(string why)
        {
            try
            {
                var lv = _lv;
                var list = _mDtList.Get(lv) as Array;
                int n = Int(_mDtNum, lv, -1);
                var seq = _mDtVoice.Get(lv) as int[];
                return "dirtyTalkType " + (_mDtType.Ok ? _mDtType.Get(lv) as string : "?") + ", list " + (list != null ? list.Length : -1) + ", randomSequence " + Int(_mRandSeq, lv, 0)
                       + ", playNum " + n + ", seq[n] " + (seq != null && n >= 0 && n < seq.Length ? seq[n].ToString() : "-") + ", voiceLanguage " + Int(_mLang, _gs, 0) + " (" + why + ")";
            }
            catch (Exception) { return why; }
        }

        // ======================================================================================================================
        // Release / Drop
        // ======================================================================================================================
        void ReleaseHeld()
        {
            try { RestoreBlush(); } catch (Exception) { }
            try { RestoreGaze(); } catch (Exception) { }
            try { RestorePitch(); } catch (Exception) { }
            if (_trigSet) { _trigSet = false; ResetTriggerNow(); }
        }

        void Drop(string why)
        {
            LogOnce("drop" + why + (int)_now, "bridge dropped (" + why + ")");
            Release();
        }

        // Gives back what we hold (blush, gaze weight, moan pitch) where the game still shows our value, destroys our sound source,
        // unsubscribes the log handler and forgets the session (Refresh finds everything again).
        public void Release()
        {
            ReleaseHeld();
            if (_guardSub) { try { Application.logMessageReceived -= _logCb; } catch (Exception) { } _guardSub = false; }
            try { if (_sfxGo != null) UnityEngine.Object.Destroy(_sfxGo); } catch (Exception) { }
            _sfxGo = null; _sfxSrc = null;
            Found = false; Missing = ""; _missing.Clear();
            _facial = _lv = _snd = _gs = _bp = _sm = _gauge = _solver = null;
            _anim = null; _herRoot = null;
            _srcMoan = _srcDirty = _srcDialogue = _srcSfx = _srcVoice = null; _tSrc = null;
            _isPlayingFast = null; _gazeGet = null; _gazeSet = null; _mGazeW = null;
            _miMoan = _miBlink = _miDesirePlus = null;
            for (int i = 0; i < _moanBanks.Length; i++) _moanBanks[i].Clips = null;
            _breath.Clips = null; _slap.Clips = null; _bankLang = -1;
            _bm.Clear(); _bmSrc.Clear(); BlushFaceCount = BlushBodyCount = 0;
            _hasMoanFn = false;
            HasMoan = HasLibrary = HasBreath = HasSfx = HasBlush = HasGaze = HasGauge = HasAnim = HasBlink = false;
            InSexMode = VoiceOn = SfxOn = BreakSystemOn = DirtyTalkPlaying = DialoguePlaying = DirtyTalkPause = FunctionPause = FacialOff = MoanPlaying = OwnVoicePlaying = false;
            FacialLayer = ""; TouchReactionTime = 0f; Desire = -1f;
            LastClip = null; LastSlapClip = null; LastClipLength = 0f;
            _gateT = -100f; _nextFind = 0f; _nextSlow = 0f; _nextSet = 0f; _ownEnd = 0f; _moanFail = 0; _moanOffUntil = 0f;
            _bridgeLogged = false; BridgeLine = "";
            _pitchHold = false; _gHold = false; _trigSet = false;
            _guardDead = false; _guardBackoff = false; _repaired = false; _voiceErr = false;
            GuardStatus = "off";
        }
    }
}
