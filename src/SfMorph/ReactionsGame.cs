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
        public bool HasSfx;                // slap clips (sex_fast / sex_normal, else handJob_fast) and the soundEffect source exist (Slap() works)
        public bool HasEyeReact;           // ReactionFacialEnter_Eye / _Out_Eye and a non-empty DirtyTalk_Facial_Library id array exist (EyeReact() works)
        public float BlushRange = 1f;     // upper end of the _BlushPower range of her skin materials (1 when unknown): SetBlush() adds up to this much
        public bool HasBlush;              // at least one of her materials has _BlushPower (SetBlush() works)
        public bool HasGaze;               // bipedIK.solvers.lookAt.IKPositionWeight found (SetGaze() works)
        public bool HasGauge;              // SexMode_State_System found (Desire is live, FeedDesire() works)
        public bool HasAnim;               // the facial Animator exists and has a "moan" trigger (WinceTrigger() works)
        public bool HasBlink;              // FacialFunction.Blink exists
        public int BlushFaceCount, BlushBodyCount;   // materials with _BlushPower, by the face/body split
        public float LastClipLength;       // seconds (at the played pitch) of the last voice clip Moan()/MoanClip()/Breath() started
        public UnityEngine.Object LastClip;       // that clip (for the event log: .name), null before the first
        public UnityEngine.Object LastSlapClip;   // the clip of the last Slap()
        public string SlapBanks = "";      // which arrays the slap sounds come from (for the log and the bridge line)
        public string BridgeLine = "";     // the one-per-session bridge log line (also logged once)
        public string GuardStatus = "off"; // the dirty-talk guard's status for the panel
        public int FeedCount;              // how many times FeedDesire() actually called the game
        public float BreathVolume = 0.5f;  // volume scale of Breath()
        // The gates, refreshed by ReadGates (plain fields, no reflection when read):
        public bool InSexMode;             // SexMode_System.sexMode || foreplayMode
        public bool VoiceOn;               // GameSetting_Function.voiceIsOn
        public bool SfxOn;                 // GameSetting_Function.soundEffectIsOn
        public bool BreakSystemOn;         // GameSetting_Function.dirtyTalkBreakSystemIsOn
        public bool DirtyTalkPlaying;      // dirtyTalk_audioSource.isPlaying (the flag below is sticky and only logged)
        public bool DirtyTalkFlag;         // LadyVoice_Function.dirtyTalkPlaying (sticky: not a gate)
        public bool DialoguePlaying;       // dialoguePlaying || kissInteractionPlaying || dialogue_audioSource.isPlaying (real scripted speech: a hard block)
        public string DialogueWhy = "";    // which of those three: "dialogue", "kiss" or "dialogue audio" (empty when none)
        public bool SexFacialPlaying;      // LadyVoice_Function.sexFacialPlaying (the feeling loops; on for long stretches in sex mode, not a block for slaps)
        public bool DirtyTalkPause;        // LadyVoice_Function.dirtyTalkPause
        public bool FunctionPause;         // FacialFunction.functionPause
        public bool FacialOff;             // FacialFunction.facialLayer == "off"
        public string FacialLayer = "";    // FacialFunction.facialLayer ("on", "lip", "eye", "eyeBrow", "off")
        public bool MoanPlaying;           // moan_audioSource.isPlaying (the game's, or ours from MoanClip)
        public bool MoanTail;              // MoanPlaying, but only the silent padding of the clip is left (the game pads its moans to 4 s): audibly free
        public bool OwnVoicePlaying;       // our Breath() one-shot is still sounding (updated on every ReadGates call)
        public float TouchReactionTime;    // LadyBreastPhysics_Function.touchReactionTime (s)
        public float Desire = -1f;         // SexMode_State_System.reikaDesire (0..100); < 0 = unknown (no gauge)
        public int Language;               // GameSetting_Function.voiceLanguage
        // 0.8.6: her recorded lines for the touched area (DirtyTalk_Library[language].reikaNipple/reikaPussy/reikaAnal/public_DT_Lv1..3) and the
        // feeling one-shots (SexVoice_Source_Library[language].Feeling_enter_0..3)
        public bool HasLines;              // at least one line clip and the dirty-talk source exist (SpeakLine() works)
        public bool HasFeeling;            // at least one Feeling_enter clip and the moan source exist (Feeling() works)
        public bool LinePathOk;            // the game's own path (DirtyTalkTypeSelect + PlayDirtyTalk and the fields restored after it) is usable
        public bool DirtyTalkOn;           // GameSetting_Function.dirtyTalkIsOn (off: the game mutes the dirty-talk source)
        public float LastLineLength;       // seconds of the last line SpeakLine() started
        public UnityEngine.Object LastLineClip;
        public string LineHow = "";        // which path the last SpeakLine() took ("game", "own clip (...)")
        public int LineCat, LineLevel;     // the category (0 nipple, 1 pussy, 2 anal, 3 public) and level (1..3) it actually used
        public static readonly string[] LineCats = { "nipple", "pussy", "anal", "public" };
        // 0.8.7: her face Animator (FacialFunction.anim) for the face clips; null while the bridge is not found
        public Animator Anim { get { return Found && _anim != null && !Dead(_anim) ? _anim : null; } }

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

        sealed class BMat { public Material M; public bool Face; public float Base, Last; public bool Hold; public bool IsRange; public float Lo, Hi; }

        // ---- the bridged game objects (cached per session) ----
        Assembly _asm; bool _asmLooked;
        object _facial, _lv, _snd, _gs, _bp, _sm, _gauge, _solver;
        Animator _anim;
        Transform _herRoot;
        UnityEngine.Object _animCtrl;
        Behaviour _srcMoan, _srcDirty, _srcDialogue, _srcSfx, _srcVoice;
        Type _tSrc;
        // members
        Mem _mLv, _mSnd, _mBp, _mAnim, _mGs, _mSm, _mFPause, _mLayer, _mBiped;
        Mem _mDtPause, _mDialogue, _mKiss, _mSexFacial, _mDtPlaying, _mLvPause, _mLib, _mSfxLib;
        Mem _mDtActive, _mDtList, _mDtVoice, _mDtNum, _mRandSeq, _mDtSpeed, _mDtType;
        Mem _mSeType, _mSeVoice, _mSeNum, _mCumVoice, _mCumNum, _mFacLib, _mMixer;
        Mem _mSrcMoan, _mSrcDirty, _mSrcDialogue, _mSrcSfx, _mSrcVoice;
        Mem _mVoiceOn, _mSfxOn, _mBreakOn, _mLang;
        Mem _mSexMode, _mForeplay, _mTouchTime, _mDesire;
        MethodInfo _miMoan, _miBlink, _miDesirePlus, _miEyeIn, _miEyeOut, _miDtStop, _miSubHide;
        Mem _mSubtitle;
        // 0.8.6 lines: the library, the game's path and every field that path changes (snapshot and restored around it)
        Mem _mDtLib, _mDtIsOn, _mReikaLv, _mPlayerLv, _mDtLen1, _mDtLen2, _mDtLen3, _mDtTypeStr, _mDtTypeList, _mDtLevelList;
        MethodInfo _miDtSelect, _miDtPlay;
        readonly Bank[] _lines = new Bank[12];                       // [category * 3 + level - 1]
        readonly UnityEngine.Object[] _feel = new UnityEngine.Object[4];
        readonly object[] _argStr = new object[1], _argBool = new object[1];
        int _lineLang = -1, _lineUses, _lineFails; float _feelEnd;
        static readonly string[] LineTypes = { "reikaNipple", "reikaPussy", "reikaAnal", "handJob" };   // handJob's lists start with public_DT_LvN
        // audio members (AudioSource / AudioClip, reflected)
        PropertyInfo _pPlaying, _pClip, _pPitch, _pVolume, _pBlend, _pRolloff, _pMin, _pMax, _pPlayAwake, _pGroup, _pClipLen, _pTime, _pLoop;
        MethodInfo _miPlay, _miStop, _miOneShot;
        Func<object, bool> _isPlayingFast;
        readonly object[] _argOneShot = new object[2], _argInt = new object[1], _argFlt = new object[1];
        // gaze accessors
        Mem _mGazeW; Func<object, float> _gazeGet; Action<object, float> _gazeSet;
        // animator
        int _hMoan, _hEyeIn, _hEyeOut; bool _hasEyeTrig;
        // clips
        readonly Bank[] _moanBanks = { new Bank(), new Bank(), new Bank() };
        readonly Bank _breath = new Bank(), _slapHard = new Bank(), _slapSoft = new Bank();
        readonly int[][] _eyeIds = new int[3][];            // DirtyTalk_Facial_Library.public_DT_Lv1..3
        string _eyeText = "";
        int _bankLang = -1;
        // our sfx source
        GameObject _sfxGo; Behaviour _sfxSrc;
        // blush
        readonly List<BMat> _bm = new List<BMat>();
        readonly List<Renderer> _bmSrc = new List<Renderer>();
        readonly List<Material> _bmFirst = new List<Material>();   // each tracked renderer's first sharedMaterial at scan time (a swapped one means rescan)
        int _idBlush;
        // trackers and timers
        bool _trigSet; float _trigT;
        float _eyeOutAt, _eyeResetAt; int _eyeK;
        int _gateMask; float _gateLogT = -100f;
        bool _slapDiag; Type _tListener; string _blushSig = "";
        bool _pitchHold; float _pitchBase, _pitchSet;
        bool _gHold; float _gBase, _gLast;
        float _now, _gateT = -100f, _nextFind, _nextSlow, _nextSet, _lastFeed = -100f, _ownEnd;
        int _moanFail; float _moanOffUntil;
        float _moanT0, _ownT0; bool _ownOneShot;   // when our/the game's last moan started; whether the latest thing on the moan source is our breath one-shot
        bool _bridgeLogged;
        readonly HashSet<string> _logged = new HashSet<string>();
        readonly List<string> _missing = new List<string>();

        // ---- guard state ----
        bool _guardSub, _guardBackoff, _dtDead;
        string _dtStatus = "off"; int _dtStKey = -1, _dtVer, _gsVer = -1, _gsSfx = -1;   // the guard's status text is built only when its inputs change
        volatile bool _voiceErr, _sfxErr;
        int _sfxRepairs, _sfxSinceLog, _sfxLogMs, _sfxErrTotal; bool _sfxLoggedFirst;
        readonly Dictionary<string, Mem[]> _seMems = new Dictionary<string, Mem[]>();
        int _errAfter, _errTotal;
        bool _repaired; int _lastRepairMs, _guardMs, _repairLogMs, _backoffUntilMs, _repairs, _repairsSinceLog;
        bool _guardLoggedFirst;
        readonly Application.LogCallback _logCb;

        public ReactionsGame() { _logCb = OnLog; for (int i = 0; i < _lines.Length; i++) _lines[i] = new Bank(); }

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
            var m = Q(t, name);
            if (!m.Ok) Miss(label);
            return m;
        }

        // The same lookup without reporting a miss (for the alternatives of a fallback list).
        Mem Q(Type t, string name)
        {
            var m = new Mem();
            if (t != null)
                for (var k = t; k != null && !m.Ok && k != typeof(object); k = k.BaseType)
                {
                    m.F = k.GetField(name, Own);
                    if (m.F == null) { var p = k.GetProperty(name, Own); if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) m.P = p; }
                }
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

        // A method without arguments (public or private), found quietly (no Miss): for optional members.
        static MethodInfo QMe(Type t, string name)
        {
            MethodInfo mi = null;
            if (t != null)
                for (var k = t; k != null && mi == null && k != typeof(object); k = k.BaseType)
                    mi = k.GetMethod(name, AnyM | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            return mi;
        }

        static object Peek(Mem m, object o) { try { return m.Get(o); } catch (Exception) { return null; } }
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
                    // a null (not destroyed) _lv or _srcMoan is "not there yet": peek at the member and re-resolve once it appears
                    bool appeared = (_lv == null && _mLv != null && _mLv.Ok && Peek(_mLv, _facial) != null)
                        || (_srcMoan == null && _snd != null && _mSrcMoan != null && _mSrcMoan.Ok && Peek(_mSrcMoan, _snd) is Behaviour);
                    if (appeared || Dead(_lv) || Dead(_snd) || Dead(_gs) || Dead(_anim) || Dead(_srcMoan) || (_srcSfx != null && Dead(_srcSfx)))
                    {
                        LogOnce("reresolve" + _nextSlow.ToString("0"), "a game object was replaced, looking its members up again");
                        ReleaseHeld();
                        Resolve(false);
                    }
                    else if (_anim != null && !Dead(_anim) && !ReferenceEquals(_anim.runtimeAnimatorController, _animCtrl))
                    {
                        bool had = HasAnim;
                        CheckAnim();
                        if (had != HasAnim) LogOnce("animctrl" + (HasAnim ? "y" : "n") + _nextSlow.ToString("0"), "the Animator's controller changed: moan trigger " + (HasAnim ? "present" : "missing"));
                    }
                    if (_gauge == null || Dead(_gauge)) FindGauge(now, false);
                    ResolveGaze();
                    bool rescan = false;
                    for (int i = 0; i < _bm.Count; i++) if (Dead(_bm[i].M)) { rescan = true; break; }
                    // a tracked renderer whose sharedMaterial changed (the game swapped materials): rescan too
                    try
                    {
                        for (int i = 0; !rescan && i < _bmSrc.Count && i < _bmFirst.Count; i++)
                            if (_bmSrc[i] != null && !ReferenceEquals(_bmSrc[i].sharedMaterial, _bmFirst[i])) rescan = true;
                    }
                    catch (Exception) { rescan = true; }
                    if (rescan) DiscoverBlush(her);
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

        // Whether the facial Animator's controller has a "moan" trigger (at resolve, and again when the controller is swapped).
        void CheckAnim()
        {
            HasAnim = false; _animCtrl = null; _hasEyeTrig = false;
            if (_anim == null || Dead(_anim)) return;
            _hMoan = Animator.StringToHash("moan");
            _hEyeIn = Animator.StringToHash("reaction_enter_Eye"); _hEyeOut = Animator.StringToHash("reaction_out_Eye");
            try
            {
                _animCtrl = _anim.runtimeAnimatorController;
                bool ein = false, eout = false;
                foreach (var p in _anim.parameters)
                {
                    if (p.type != AnimatorControllerParameterType.Trigger) continue;
                    if (p.nameHash == _hMoan) HasAnim = true;
                    else if (p.nameHash == _hEyeIn) ein = true;
                    else if (p.nameHash == _hEyeOut) eout = true;
                }
                _hasEyeTrig = ein && eout;
            }
            catch (Exception e) { LogOnce("animparams", "Animator parameters unreadable: " + e.Message); HasAnim = true; }
            if (!HasAnim) Miss("Animator trigger 'moan'");
            else if (_missing.Remove("Animator trigger 'moan'")) Missing = string.Join(", ", _missing.ToArray());
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
            _miEyeIn = Me(tf, "ReactionFacialEnter_Eye", "FacialFunction.ReactionFacialEnter_Eye(int)", typeof(int));
            _miEyeOut = Me(tf, "ReactionFacialOut_Eye", "FacialFunction.ReactionFacialOut_Eye(float)", typeof(float));
            _mFacLib = M(tf, "dirtyTalk_Facial_Library", "FacialFunction.dirtyTalk_Facial_Library");
            _hasMoanFn = _miMoan != null; HasMoan = _hasMoanFn; HasBlink = _miBlink != null;
            // optional (no Miss): how the game stops dirty talk when a moan breaks in
            _miDtStop = QMe(tf, "DirtyTalkFacialAnimationStop");
            _mSubtitle = Q(tf, "subtitle_Function");
            _miSubHide = null;
            try { var sub = _mSubtitle.Ok ? _mSubtitle.Get(_facial) : null; if (sub != null && !Dead(sub)) _miSubHide = QMe(sub.GetType(), "SubtitleHide"); } catch (Exception) { }

            _lv = _mLv.Get(_facial); _snd = _mSnd.Get(_facial); _bp = _mBp.Get(_facial); _gs = _mGs.Get(_facial); _sm = _mSm.Get(_facial);
            _anim = _mAnim.Get(_facial) as Animator;
            if (Dead(_lv)) _lv = null;
            if (Dead(_snd)) _snd = null;

            // animator parameters: only fire a trigger the controller has (a missing one would warn every call)
            CheckAnim();

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
            _mSeType = M(tl, "soundEffectType", "LadyVoice_Function.soundEffectType");
            _mSeVoice = M(tl, "sexSoundEffectVoice", "LadyVoice_Function.sexSoundEffectVoice");
            _mSeNum = M(tl, "sexSeNum", "LadyVoice_Function.sexSeNum");
            _mCumVoice = M(tl, "cumSoundEffectVoice", "LadyVoice_Function.cumSoundEffectVoice");
            _mCumNum = M(tl, "cumSeNum", "LadyVoice_Function.cumSeNum");
            _seMems.Clear(); _dtDead = false;

            // Sound_Function: the sources
            var ts = _snd != null ? _snd.GetType() : Ty("Sound_Function");
            _mSrcMoan = M(ts, "moan_audioSource", "Sound_Function.moan_audioSource");
            _mSrcDirty = M(ts, "dirtyTalk_audioSource", "Sound_Function.dirtyTalk_audioSource");
            _mSrcDialogue = M(ts, "dialogue_audioSource", "Sound_Function.dialogue_audioSource");
            _mSrcSfx = M(ts, "soundEffect_audioSource", "Sound_Function.soundEffect_audioSource");
            _mSrcVoice = M(ts, "voice_audioSource", "Sound_Function.voice_audioSource");
            _mMixer = Q(ts, "audioMixer");
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

            // 0.8.6 lines (optional: no Miss, the toggle's status says what is missing)
            _mDtLib = Q(tl, "dirtyTalk_Library");
            _mDtIsOn = Q(tg, "dirtyTalkIsOn");
            _mReikaLv = Q(tf, "dirtyTalk_reika_level"); _mPlayerLv = Q(tf, "dirtyTalk_player_level");
            _mDtLen1 = Q(tl, "dtClipLength_1"); _mDtLen2 = Q(tl, "dtClipLength_2"); _mDtLen3 = Q(tl, "dtClipLength_3");
            _mDtTypeStr = Q(tl, "dirtyTalkTypeList_string"); _mDtTypeList = Q(tl, "dirtyTalkTypeList"); _mDtLevelList = Q(tl, "dirtyTalkLevelList");
            _miDtSelect = null; _miDtPlay = null;
            if (tl != null)
                for (var k = tl; k != null && k != typeof(object) && (_miDtSelect == null || _miDtPlay == null); k = k.BaseType)
                {
                    if (_miDtSelect == null) _miDtSelect = k.GetMethod("DirtyTalkTypeSelect", AnyM | BindingFlags.DeclaredOnly, null, new[] { typeof(string) }, null);
                    if (_miDtPlay == null) _miDtPlay = k.GetMethod("PlayDirtyTalk", AnyM | BindingFlags.DeclaredOnly, null, new[] { typeof(bool) }, null);
                }
            _lineLang = -1; _lineFails = 0;

            BuildLibrary(Int(_mLang, _gs, 0));
            HasMoan = _hasMoanFn || HasLibrary;
            BuildSfx();
            BuildEye();
        }

        void ResolveAudioMembers()
        {
            _pPlaying = _pClip = _pPitch = _pVolume = _pBlend = _pRolloff = _pMin = _pMax = _pPlayAwake = _pGroup = _pClipLen = _pTime = _pLoop = null;
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
            _pTime = _tSrc.GetProperty("time");
            _pLoop = _tSrc.GetProperty("loop");
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
            BuildLines(lang);
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
                // the real fields are Exhale_f32/f64/f128_slow|normal|fast: try the slow trio, then the normal trio (no Miss for the alternatives)
                UnityEngine.Object[] ex = null; string exName = null;
                string[] exNames = { "Exhale_f32_slow", "Exhale_f64_slow", "Exhale_f128_slow", "Exhale_f32_normal", "Exhale_f64_normal", "Exhale_f128_normal" };
                for (int e = 0; e < exNames.Length && (ex == null || ex.Length == 0); e++)
                {
                    var mf = Q(tl, exNames[e]);
                    if (!mf.Ok) continue;
                    ex = ToClips(mf.Get(lib) as Array); exName = exNames[e];
                }
                if (ex != null && ex.Length > 0 && _miOneShot != null)
                {
                    _breath.Clips = ex; HasBreath = true;
                    var sbx = new System.Text.StringBuilder();
                    for (int i = 0; i < ex.Length; i++) { if (i > 0) sbx.Append(", "); sbx.Append(ClipLen(ex[i]).ToString("0.00")).Append('s'); }
                    LogOnce("breath" + lang, "breath clips: " + exName + ", " + ex.Length + " clip(s) (" + sbx + ")");
                }
                else if (ex == null) Miss("SexVoice_Source_Library.Exhale_f32_slow/_normal");
            }
            catch (Exception e) { LogOnce("buildlib", "voice library unreadable: " + e.Message); HasLibrary = false; }
            if (_bridgeLogged) Log("voice language " + lang + ": " + BanksText());
        }

        // 0.8.6: the language's line clips by category and level, and the four Feeling_enter one-shots. Logged once per language with the counts.
        void BuildLines(int lang)
        {
            for (int i = 0; i < _lines.Length; i++) { _lines[i].Clips = null; for (int k = 0; k < 4; k++) _lines[i].Recent[k] = -1; }
            for (int i = 0; i < _feel.Length; i++) _feel[i] = null;
            HasLines = false; HasFeeling = false;
            LinePathOk = _miDtSelect != null && _miDtPlay != null && _mDtList.Ok && _mDtVoice.Ok && _mDtNum.Ok && _mDtType.Ok && _mDtActive.Ok
                      && _mReikaLv.Ok && _mPlayerLv.Ok && _mDtLen1.Ok && _mDtLen2.Ok && _mDtLen3.Ok && _mDtTypeStr.Ok && _mDtTypeList.Ok && _mDtLevelList.Ok
                      && _pClip != null && _srcDirty != null;
            if (_lv == null) return;
            var sb = new StringBuilder();
            sb.Append("lines for voice language ").Append(lang).Append(": ");
            try
            {
                var libs = _mDtLib != null && _mDtLib.Ok ? _mDtLib.Get(_lv) as Array : null;
                var lib = libs != null && lang >= 0 && lang < libs.Length ? libs.GetValue(lang) : null;
                if (lib == null || Dead(lib)) sb.Append("no DirtyTalk_Library (").Append(libs == null ? "no array" : libs.Length + " entries").Append(')');
                else
                {
                    var tl = lib.GetType();
                    string[] fields = { "reikaNipple_DT_Lv", "reikaPussy_DT_Lv", "reikaAnal_DT_Lv", "public_DT_Lv" };
                    for (int c = 0; c < 4; c++)
                    {
                        if (c > 0) sb.Append(", ");
                        sb.Append(LineCats[c]).Append(' ');
                        for (int l = 0; l < 3; l++)
                        {
                            var m = Q(tl, fields[c] + (l + 1));
                            var clips = m.Ok ? ToClips(m.Get(lib) as Array) : null;
                            _lines[c * 3 + l].Clips = clips != null && clips.Length > 0 ? clips : null;
                            if (_lines[c * 3 + l].Clips != null && _srcDirty != null && _pClip != null && _miPlay != null) HasLines = true;
                            if (l > 0) sb.Append('/');
                            sb.Append(m.Ok ? (clips != null ? clips.Length.ToString() : "0") : "-");
                        }
                    }
                    var e0 = Q(tl, "empty");
                    var empty = e0.Ok ? e0.Get(lib) as Array : null;
                    sb.Append(" (Lv1/Lv2/Lv3; 'empty' holds ").Append(empty != null ? empty.Length.ToString() : "-").Append(')');
                }
            }
            catch (Exception e) { sb.Append("unreadable: ").Append(e.Message); }
            try
            {
                var libs = _mLib.Ok ? _mLib.Get(_lv) as Array : null;
                var lib = libs != null && lang >= 0 && lang < libs.Length ? libs.GetValue(lang) : null;
                sb.Append("; feeling enter ");
                if (lib == null) sb.Append("none");
                else
                {
                    var tl = lib.GetType();
                    for (int i = 0; i < 4; i++)
                    {
                        var m = Q(tl, "Feeling_enter_" + i);
                        _feel[i] = m.Ok ? m.Get(lib) as UnityEngine.Object : null;
                        if (_feel[i] == null) _feel[i] = null;   // a destroyed or missing clip
                        if (_feel[i] != null && _srcMoan != null && _pClip != null && _miPlay != null) HasFeeling = true;
                        if (i > 0) sb.Append(", ");
                        sb.Append(i).Append(' ').Append(_feel[i] != null ? _feel[i].name + " " + ClipLen(_feel[i]).ToString("0.00") + "s" : "-");
                    }
                }
            }
            catch (Exception e) { sb.Append("unreadable: ").Append(e.Message); }
            sb.Append("; the game's path (DirtyTalkTypeSelect + PlayDirtyTalk) ").Append(LinePathOk ? "usable" : "not usable, own clips on the dirty-talk source");
            if (!LinePathOk)
            {
                sb.Append(" (missing:");
                if (_miDtSelect == null) sb.Append(" DirtyTalkTypeSelect(string)");
                if (_miDtPlay == null) sb.Append(" PlayDirtyTalk(bool)");
                if (!_mReikaLv.Ok) sb.Append(" dirtyTalk_reika_level");
                if (!_mPlayerLv.Ok) sb.Append(" dirtyTalk_player_level");
                if (!_mDtLen1.Ok || !_mDtLen2.Ok || !_mDtLen3.Ok) sb.Append(" dtClipLength_1..3");
                if (!_mDtTypeStr.Ok || !_mDtTypeList.Ok || !_mDtLevelList.Ok) sb.Append(" dirtyTalkTypeList/LevelList");
                if (!_mDtList.Ok || !_mDtVoice.Ok || !_mDtNum.Ok || !_mDtType.Ok || !_mDtActive.Ok) sb.Append(" dirty-talk list/shuffle/type");
                if (_srcDirty == null) sb.Append(" dirtyTalk_audioSource");
                sb.Append(')');
            }
            if (lang != _lineLang) { _lineLang = lang; Log(sb.ToString()); }
        }

        Bank LineBank(int cat, int level) { return cat >= 0 && cat < 4 && level >= 1 && level <= 3 ? _lines[cat * 3 + level - 1] : null; }

        // How many clips a category has at a level (0 = none).
        public int LineCount(int cat, int level) { var b = LineBank(cat, level); return b != null ? b.Count : 0; }

        // One of her recorded lines for the touched area: category 0 nipple, 1 pussy, 2 anal, 3 public, level 1..3. An empty level falls back to the
        // lower levels of the category, then to the public lines. The game's own path when it is usable (her talking face and subtitle come with
        // it), else the clip on the game's dirty-talk source. The arbiter is Reactions' (never over dialogue, a kiss, dirty talk or a moan).
        // gamePath: [Reactions] LinesGamePath (off: always the clip on the dirty-talk source).
        public bool SpeakLine(int cat, int level, bool gamePath, out string how)
        {
            how = "";
            if (!Found || !HasLines || _lv == null || Dead(_lv) || _srcDirty == null || Dead(_srcDirty)) { how = "no lines"; return false; }
            Bank b = null; int uc = cat, ul = level;
            for (int l = level; l >= 1 && b == null; l--) { var x = LineBank(cat, l); if (x != null && x.Count > 0) { b = x; ul = l; } }
            if (b == null) for (int l = level; l >= 1 && b == null; l--) { var x = LineBank(3, l); if (x != null && x.Count > 0) { b = x; uc = 3; ul = l; } }
            if (b == null) for (int l = 1; l <= 3 && b == null; l++) { var x = LineBank(3, l); if (x != null && x.Count > 0) { b = x; uc = 3; ul = l; } }
            if (b == null) { how = "no clips for " + (cat >= 0 && cat < 4 ? LineCats[cat] : "?"); return false; }
            var clip = b.Pick();
            if (clip == null) { how = "no clip"; return false; }
            string why = null;
            bool ok = false;
            if (LinePathOk && gamePath) { ok = LineViaGame(uc, ul, clip, out why); if (ok) how = "game"; }
            else why = !gamePath ? "the game's path is off in the config" : "the game's path is not usable";
            if (!ok)
            {
                ok = LineOwn(clip);
                how = ok ? "own clip (" + why + ")" : "failed (" + why + ")";
            }
            if (!ok) return false;
            LastLineClip = clip; LastLineLength = ClipLen(clip); LineCat = uc; LineLevel = ul; LineHow = how;
            _lineUses++;
            if (_lineUses <= 10)
                Log("line " + _lineUses + ": " + LineCats[uc] + " Lv" + ul + (uc != cat || ul != level ? " (asked " + LineCats[Mathf.Clamp(cat, 0, 3)] + " Lv" + level + ")" : "")
                    + " '" + clip.name + "' " + LastLineLength.ToString("0.00") + " s via " + how);
            return true;
        }

        // The game's path: the level on FacialFunction, DirtyTalkTypeSelect(type) builds the type's list, a one-entry shuffle at our clip's index makes
        // PlayDirtyTalk(true) play exactly that clip (and DirtyTalkNumber map it to its face and subtitle); then every field the two calls wrote is
        // put back: type, active flag, list, its segment lengths and type/level lists, the shuffle and its counter, the level. The face and subtitle
        // already got their numbers as arguments, so they are not affected. The dirty-talk guard sees only the restored, valid state.
        // No game code reads the shuffle later (IL, 0.8.8): dirtyTalkVoice / dirtyTalkPlayNum are read only by PlayRandomVoice and
        // DirtyTalkNumber (whose one caller is PlayDirtyTalk, synchronously, inside this call) and printed by an uncalled debug method; the
        // DirtyTalkEyeFacialType coroutine gets type / level / num as arguments and reads only the face library.
        bool LineViaGame(int cat, int level, UnityEngine.Object clip, out string why)
        {
            why = null;
            var lvMem = cat < 3 ? _mReikaLv : _mPlayerLv;
            object lv = _lv;
            object oType = _mDtType.Get(lv), oActive = _mDtActive.Get(lv), oList = _mDtList.Get(lv), oSeq = _mDtVoice.Get(lv), oNum = _mDtNum.Get(lv);
            object oL1 = _mDtLen1.Get(lv), oL2 = _mDtLen2.Get(lv), oL3 = _mDtLen3.Get(lv);
            object oTS = _mDtTypeStr.Get(lv), oTL = _mDtTypeList.Get(lv), oLL = _mDtLevelList.Get(lv);
            object oLevel = lvMem.Get(_facial);
            bool ok = false;
            try
            {
                lvMem.Set(_facial, level);
                _argStr[0] = LineTypes[cat];
                _miDtSelect.Invoke(lv, _argStr);
                var list = _mDtList.Get(lv) as Array;
                int idx = list != null ? Array.IndexOf(list, clip) : -1;
                if (idx < 0) why = "the game's " + LineTypes[cat] + " Lv" + level + " list lacks the clip";
                else
                {
                    _mDtVoice.Set(lv, new int[] { idx });
                    _mDtNum.Set(lv, 0);
                    _argBool[0] = true;
                    _miDtPlay.Invoke(lv, _argBool);
                    ok = ReferenceEquals(_pClip.GetValue(_srcDirty, null), clip) && Playing(_srcDirty);
                    if (!ok) why = "PlayDirtyTalk did not start the clip";
                    var oSeqArr = oSeq as int[];
                    LogOnce("linefirst", "lines: first use of the game's path: " + LineTypes[cat] + " Lv" + level + ", clip '" + clip.name + "' at index " + idx
                        + " of a list of " + list.Length + " (first segment " + Int(_mDtLen1, lv, -1) + "); the game's own shuffle (playNum " + (oNum ?? "?")
                        + ", " + (oSeqArr != null ? oSeqArr.Length + " entries" : "none") + ", list of " + (oList is Array ? ((Array)oList).Length.ToString() : "-")
                        + ", type " + (oType ?? "-") + ") restored right after; played: " + (ok ? "yes" : "no"));
                }
            }
            catch (Exception e)
            {
                var ie = e.InnerException ?? e;
                why = "the game's path threw " + ie.GetType().Name + ": " + ie.Message;
                if (++_lineFails >= 3) { LinePathOk = false; LogOnce("linepathoff", "lines: the game's path failed 3 times, own clips on the dirty-talk source from now on (" + why + ")"); }
            }
            finally
            {
                try
                {
                    _mDtType.Set(lv, oType); _mDtActive.Set(lv, oActive); _mDtList.Set(lv, oList); _mDtVoice.Set(lv, oSeq); _mDtNum.Set(lv, oNum);
                    _mDtLen1.Set(lv, oL1); _mDtLen2.Set(lv, oL2); _mDtLen3.Set(lv, oL3);
                    _mDtTypeStr.Set(lv, oTS); _mDtTypeList.Set(lv, oTL); _mDtLevelList.Set(lv, oLL);
                    if (oLevel != null) lvMem.Set(_facial, oLevel);
                    HoldDirtyTimer();
                }
                catch (Exception e) { LogOnce("linerestore", "lines: restoring the game's dirty-talk state failed: " + e.Message); }
            }
            if (ok) _lineFails = 0;
            return ok;
        }

        // The clip on the game's dirty-talk source (the game's timer waits while it plays), the voice loop stopped like the game does. No face.
        bool LineOwn(UnityEngine.Object clip)
        {
            try
            {
                if (_pClip == null || _miPlay == null) return false;
                _pClip.SetValue(_srcDirty, clip, null);
                _miPlay.Invoke(_srcDirty, null);
                if (_srcVoice != null && !Dead(_srcVoice) && _miStop != null) _miStop.Invoke(_srcVoice, null);
                HoldDirtyTimer();
                return true;
            }
            catch (Exception e) { LogOnce("lineown:" + e.GetType().Name, "lines: own clip failed: " + e.Message); return false; }
        }

        // The game's own dirty talk does not follow ours at once: its countdown gets at least 3 s (it only runs while the source is silent).
        void HoldDirtyTimer()
        {
            if (_mDtSpeed != null && _mDtSpeed.Ok && Flt(_mDtSpeed, _lv, 3f) < 3f) _mDtSpeed.Set(_lv, 3f);
        }

        // A Feeling_enter one-shot (0..3) on the moan source with the "moan" trigger for her mouth; the moan source counts as busy for its whole length.
        public bool Feeling(int n)
        {
            n = Mathf.Clamp(n, 0, 3);
            if (!Found || !HasFeeling || _srcMoan == null || Dead(_srcMoan)) return false;
            var clip = _feel[n];
            for (int d = 1; clip == null && d < 4; d++) clip = _feel[Mathf.Clamp(n - d, 0, 3)] ?? _feel[Mathf.Clamp(n + d, 0, 3)];
            if (clip == null) return false;
            try
            {
                RestorePitch();
                if (_miStop != null && (Playing(_srcMoan) || _now < _ownEnd)) { _miStop.Invoke(_srcMoan, null); _ownEnd = 0f; OwnVoicePlaying = false; }
                _moanT0 = _now; _ownOneShot = false; _packClip = false;
                _pClip.SetValue(_srcMoan, clip, null);
                _miPlay.Invoke(_srcMoan, null);
                if (_srcVoice != null && !Dead(_srcVoice) && _miStop != null) _miStop.Invoke(_srcVoice, null);
                LastClip = clip; LastClipLength = ClipLen(clip);
                _feelEnd = _now + LastClipLength;
                if (HasAnim) { _anim.SetTrigger(_hMoan); MarkTrigger(); }
                LogOnce("feeling" + n, "feeling: Feeling_enter_" + n + " '" + clip.name + "' " + LastClipLength.ToString("0.00") + " s on the moan source");
                return true;
            }
            catch (Exception e) { LogOnce("feeling:" + e.GetType().Name, "feeling clip failed: " + e.Message); return false; }
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

        // The slap clips: sex_fast (the skin-on-skin impact of the thrusts) for hard slaps, sex_normal for soft ones, handJob_fast as the
        // fallback for either (it sounds like hand-job stroking, so it is last). SoundEffect_Library is not per language.
        void BuildSfx()
        {
            _slapHard.Clips = null; _slapSoft.Clips = null;
            for (int k = 0; k < 4; k++) { _slapHard.Recent[k] = -1; _slapSoft.Recent[k] = -1; }
            HasSfx = false; SlapBanks = "";
            if (!_mSfxLib.Ok || _lv == null || _srcSfx == null || _miOneShot == null) { return; }
            try
            {
                var lib = _mSfxLib.Get(_lv);
                if (lib == null) { Miss("LadyVoice_Function.soundEffect_Library (null)"); return; }
                var tl = lib.GetType();
                var fast = ToClips(Q(tl, "sex_fast").Get(lib) as Array);
                var normal = ToClips(Q(tl, "sex_normal").Get(lib) as Array);
                UnityEngine.Object[] hand = null;
                if (fast == null || fast.Length == 0 || normal == null || normal.Length == 0)
                {
                    hand = ToClips(M(tl, "handJob_fast", "SoundEffect_Library.handJob_fast").Get(lib) as Array);
                    if (hand != null && hand.Length == 0) hand = null;
                }
                bool hardOk = fast != null && fast.Length > 0, softOk = normal != null && normal.Length > 0;
                _slapHard.Clips = hardOk ? fast : hand;
                _slapSoft.Clips = softOk ? normal : (hardOk ? fast : hand);
                HasSfx = _slapHard.Count > 0 && _slapSoft.Count > 0;
                SlapBanks = "hard " + (hardOk ? "sex_fast" : "handJob_fast") + " (" + _slapHard.Count + "), soft "
                          + (softOk ? "sex_normal" : (hardOk ? "sex_fast" : "handJob_fast")) + " (" + _slapSoft.Count + ")";
                LogOnce("slapbanks", "slap sound clips: " + SlapBanks);
            }
            catch (Exception e) { LogOnce("buildsfx", "sound effect library unreadable: " + e.Message); }
        }

        // The game's eye reaction ids (DirtyTalk_Facial_Library.public_DT_Lv1..3, Int32[]): level 1 soft, 2 hard, 3 too hard. Logged once.
        void BuildEye()
        {
            HasEyeReact = false; _eyeIds[0] = _eyeIds[1] = _eyeIds[2] = null; _eyeText = "";
            if (_miEyeIn == null || _miEyeOut == null || !_mFacLib.Ok) return;
            try
            {
                var lib = _mFacLib.Get(_facial);
                if (lib == null || Dead(lib)) { LogOnce("eyelibnull", "eye reaction: FacialFunction.dirtyTalk_Facial_Library is null"); return; }
                var tl = lib.GetType();
                var sb = new StringBuilder();
                bool any = false;
                for (int lv = 0; lv < 3; lv++)
                {
                    var arr = Q(tl, "public_DT_Lv" + (lv + 1)).Get(lib) as int[];
                    _eyeIds[lv] = arr != null && arr.Length > 0 ? arr : null;
                    if (_eyeIds[lv] != null) any = true;
                    if (lv > 0) sb.Append(" | ");
                    sb.Append("Lv").Append(lv + 1).Append(' ');
                    if (arr == null) sb.Append("null"); else { sb.Append('['); for (int i = 0; i < arr.Length; i++) { if (i > 0) sb.Append(','); sb.Append(arr[i]); } sb.Append(']'); }
                }
                _eyeText = sb.ToString();
                HasEyeReact = any;
                LogOnce("eyeids", "eye reaction ids (public_DT_Lv1..3): " + _eyeText + (any ? "" : " (none usable: the wince stays)") + (_hasEyeTrig ? "" : "; the Animator has no reaction_enter_Eye/reaction_out_Eye trigger pair"));
            }
            catch (Exception e) { LogOnce("buildeye", "eye reaction ids unreadable: " + e.Message); HasEyeReact = false; }
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

        // Only her skin: materials whose name contains "body", "face" or "foot" (the game's reika_mouth / teeth / uterus / rectum materials
        // have the property too and must stay as they are).
        static bool SkinMaterial(string mn)
        {
            return mn.Contains("body") || mn.Contains("face") || mn.Contains("foot");
        }

        void DiscoverBlush(Character her)
        {
            RestoreBlush();
            _bm.Clear(); _bmSrc.Clear(); _bmFirst.Clear();
            BlushFaceCount = BlushBodyCount = 0;
            if (_idBlush == 0) _idBlush = Shader.PropertyToID("_BlushPower");
            var names = new List<string>();
            try
            {
                foreach (var r in her.Renderers)
                {
                    if (r == null) continue;
                    _bmSrc.Add(r); _bmFirst.Add(r.sharedMaterial);
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null || !m.HasProperty(_idBlush)) continue;
                        string mn = m.name.ToLowerInvariant();
                        if (!SkinMaterial(mn)) continue;
                        bool dup = false;
                        for (int k = 0; k < _bm.Count; k++) if (_bm[k].M == m) { dup = true; break; }
                        if (dup) continue;
                        string rn = r.name.ToLowerInvariant();
                        bool face = rn.Contains("face") || rn.Contains("head") || mn.Contains("face") || mn.Contains("head");
                        var bm = new BMat { M = m, Face = face, Base = m.GetFloat(_idBlush) };
                        bm.Last = bm.Base;
                        bm.IsRange = PropRange(m, "_BlushPower", out bm.Lo, out bm.Hi);
                        _bm.Add(bm);
                        names.Add(m.name);
                        if (face) BlushFaceCount++; else BlushBodyCount++;
                    }
                }
            }
            catch (Exception e) { LogOnce("blushscan", "blush materials unreadable: " + e.Message); }
            HasBlush = _bm.Count > 0;
            float rng = 0f;
            for (int i = 0; i < _bm.Count; i++) if (_bm[i].IsRange && _bm[i].Hi > rng) rng = _bm[i].Hi;
            BlushRange = rng > 0.01f ? rng : 1f;
            names.Sort(StringComparer.Ordinal);
            string sig = string.Join("|", names.ToArray());
            // logged when the set of materials changed (the order of a re-scan varies and is not a change)
            if (_bridgeLogged && sig != _blushSig) Log("blush materials re-read: " + BlushText());
            _blushSig = sig;
        }

        // The range of a shader Range property (Shader.GetPropertyRangeLimits through reflection: it only exists in newer Unity versions).
        struct RangeInfo { public string Prop; public bool Ok; public float Lo, Hi; }
        static readonly Dictionary<Shader, RangeInfo> _rangeCache = new Dictionary<Shader, RangeInfo>();

        // Cached per Shader instance (the scan runs on every material swap and the property walk is reflection).
        static bool PropRange(Material m, string prop, out float lo, out float hi)
        {
            lo = 0f; hi = 1f;
            try
            {
                var sh = m.shader;
                if (sh == null) return false;
                RangeInfo ri;
                if (_rangeCache.TryGetValue(sh, out ri) && ri.Prop == prop) { if (ri.Ok) { lo = ri.Lo; hi = ri.Hi; } return ri.Ok; }
                ri = new RangeInfo { Prop = prop, Ok = PropRangeScan(sh, prop, out lo, out hi), Lo = lo, Hi = hi };
                _rangeCache[sh] = ri;
                return ri.Ok;
            }
            catch (Exception) { }
            return false;
        }

        static bool PropRangeScan(Shader sh, string prop, out float lo, out float hi)
        {
            lo = 0f; hi = 1f;
            try
            {
                var t = typeof(Shader);
                var mCount = t.GetMethod("GetPropertyCount", Type.EmptyTypes);
                var mName = t.GetMethod("GetPropertyName", new[] { typeof(int) });
                var mType = t.GetMethod("GetPropertyType", new[] { typeof(int) });
                var mLim = t.GetMethod("GetPropertyRangeLimits", new[] { typeof(int) });
                if (mCount == null || mName == null || mType == null || mLim == null) return false;
                int n = Convert.ToInt32(mCount.Invoke(sh, null));
                var a = new object[1];
                for (int i = 0; i < n; i++)
                {
                    a[0] = i;
                    if ((string)mName.Invoke(sh, a) != prop) continue;
                    if (Convert.ToInt32(mType.Invoke(sh, a)) != 3) return false;   // ShaderPropertyType.Range
                    var v = (Vector2)mLim.Invoke(sh, a);
                    lo = Mathf.Min(v.x, v.y); hi = Mathf.Max(v.x, v.y);
                    return hi > lo;
                }
            }
            catch (Exception) { }
            return false;
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
                if (b.IsRange) sb.Append(" range ").Append(b.Lo.ToString("0.###")).Append("..").Append(b.Hi.ToString("0.###"));
                else sb.Append(" (not a Range property: scale 1)");
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
            sb.Append("; lines ").Append(HasLines ? (LinePathOk ? "yes (the game's path)" : "yes (own clips)") : "none").Append(", feeling enter ").Append(HasFeeling ? "yes" : "none");
            sb.Append("; slap clips ").Append(SlapBanks.Length > 0 ? SlapBanks : "none");
            sb.Append("; eye reaction ").Append(HasEyeReact ? _eyeText : "none");
            sb.Append("; blush (skin only) ").Append(BlushText());
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
                VoiceOn = _gs == null || _mVoiceOn == null || !_mVoiceOn.Ok || Bool(_mVoiceOn, _gs);
                SfxOn = _gs == null || _mSfxOn == null || !_mSfxOn.Ok || Bool(_mSfxOn, _gs);
                BreakSystemOn = Bool(_mBreakOn, _gs);
                DirtyTalkOn = _gs == null || _mDtIsOn == null || !_mDtIsOn.Ok || Bool(_mDtIsOn, _gs);
                int lang = Int(_mLang, _gs, 0);
                Language = lang;
                if (lang != _bankLang) { BuildLibrary(lang); HasMoan = _hasMoanFn || HasLibrary; }
                DirtyTalkPause = Bool(_mDtPause, _lv);
                // the game's dirtyTalkPlaying flag is sticky (only PlayDirtyTalk(bool) writes it, the timer path never clears it): only the source counts
                DirtyTalkFlag = Bool(_mDtPlaying, _lv);
                DirtyTalkPlaying = Playing(_srcDirty);
                bool dlg = Bool(_mDialogue, _lv), kiss = Bool(_mKiss, _lv), dsrc = Playing(_srcDialogue);
                SexFacialPlaying = Bool(_mSexFacial, _lv);
                DialoguePlaying = dlg || kiss || dsrc;
                DialogueWhy = dlg ? "dialogue" : kiss ? "kiss" : dsrc ? "dialogue audio" : "";
                int mask = (dlg ? 1 : 0) | (kiss ? 2 : 0) | (dsrc ? 4 : 0);   // sexFacial flips constantly in sex mode: not a reason to log
                if (mask != _gateMask && _now - _gateLogT >= 10f)
                {
                    _gateMask = mask; _gateLogT = _now;
                    Log("voice gates: dialogue " + (dlg ? "on" : "off") + ", kiss " + (kiss ? "on" : "off") + ", dialogue audio " + (dsrc ? "on" : "off")
                        + " (these block the voice); sexFacial " + (SexFacialPlaying ? "on" : "off") + " (blocks only caresses and grope), dirty-talk audio "
                        + (DirtyTalkPlaying ? "on" : "off") + " (flag " + (DirtyTalkFlag ? "on" : "off") + ", sticky, ignored)");
                }
                FunctionPause = _mFPause.Ok ? Bool(_mFPause, _facial) : Bool(_mLvPause, _lv);
                var layer = _mLayer.Ok ? _mLayer.Get(_facial) as string : null;
                FacialLayer = layer ?? "";
                FacialOff = layer != null && layer == "off";
                MoanPlaying = Playing(_srcMoan);
                MoanTail = MoanPlaying && TailSilent();
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

        // Every frame (cheap): the trigger safety and the eye reaction's delayed exit.
        public void Pump(float now)
        {
            _now = now;
            TickTrigger();
            TickEye();
            OwnVoicePlaying = now < _ownEnd;
        }

        // ---- the game's eye reaction (FacialFunction.ReactionFacialEnter_Eye(k) now, ReactionFacialOut_Eye(k) 0.6-1.0 s later) ----
        // level 1 soft, 2 hard, 3 too hard; k is a random id from public_DT_Lv<level>. True = the eye reaction is running (this call's, fresh = true, or one
        // already in progress, fresh = false). Both calls only act when the game's facialLayer allows ("on" / "eye").
        public bool EyeReact(int level, out int k, out bool fresh)
        {
            k = -1; fresh = false;
            if (!Found || !HasEyeReact || !_hasEyeTrig || Dead(_facial)) return false;
            if (_eyeOutAt > 0f) { k = _eyeK; return true; }   // already in progress: not a new one
            if (FacialLayer != "on" && FacialLayer != "eye") return false;
            int[] ids = null;
            for (int d = 0; d < 3 && ids == null; d++)
            {
                int lv = Mathf.Clamp(level - 1 - d, 0, 2);
                ids = _eyeIds[lv];
            }
            for (int lv = 0; lv < 3 && ids == null; lv++) ids = _eyeIds[lv];
            if (ids == null) return false;
            try
            {
                k = ids[UnityEngine.Random.Range(0, ids.Length)];
                _argInt[0] = k;
                _miEyeIn.Invoke(_facial, _argInt);
                _eyeK = k; _eyeOutAt = _now + 0.6f + UnityEngine.Random.value * 0.4f; _eyeResetAt = 0f;
                fresh = true;
                return true;
            }
            catch (Exception e) { LogOnce("eyein:" + e.GetType().Name, "ReactionFacialEnter_Eye threw " + (e.InnerException ?? e).Message); HasEyeReact = false; return false; }
        }

        void TickEye()
        {
            if (_eyeOutAt > 0f && (_now >= _eyeOutAt || _eyeOutAt - _now > 5f)) FinishEye();
            if (_eyeResetAt > 0f && (_now >= _eyeResetAt || _eyeResetAt - _now > 5f))
            {
                _eyeResetAt = 0f;
                try { if (_anim != null && !Dead(_anim) && _hasEyeTrig) { _anim.ResetTrigger(_hEyeIn); _anim.ResetTrigger(_hEyeOut); } }
                catch (Exception e) { LogOnce("eyereset", "eye trigger reset failed: " + e.Message); }
            }
        }

        // The exit of the eye reaction (also when we let go of the game).
        void FinishEye()
        {
            if (_eyeOutAt <= 0f) return;
            _eyeOutAt = 0f; _eyeResetAt = _now + 0.5f;
            try
            {
                if (_miEyeOut != null && _facial != null && !Dead(_facial)) { _argFlt[0] = (float)_eyeK; _miEyeOut.Invoke(_facial, _argFlt); }
            }
            catch (Exception e) { LogOnce("eyeout:" + e.GetType().Name, "ReactionFacialOut_Eye threw " + (e.InnerException ?? e).Message); }
        }

        // The game's own touch reaction: MoanFunction() (Animator "moan" + a Moan clip + the dirty-talk pause). The arbiter is Reactions'.
        public bool Moan()
        {
            if (!Found || !_hasMoanFn || Dead(_facial)) return false;
            if (_now < _moanOffUntil) return false;
            try
            {
                RestorePitch();
                _moanT0 = _now; _ownOneShot = false; _packClip = false;
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
        public bool MoanClip(int bank, float pitch, bool stopDirty, bool trigger = true)
        {
            if (!Found || !HasLibrary || _srcMoan == null || Dead(_srcMoan)) return false;
            try
            {
                var b = _moanBanks[Mathf.Clamp(bank, 0, _moanBanks.Length - 1)];
                var clip = b.Pick();
                if (clip == null) return false;
                if (!_pitchHold) _pitchBase = GetPitch(_srcMoan, 1f);
                // like the game's DirtyTalkMoan: talking stops for the moan; a moan or breath still sounding is cut
                if (stopDirty) StopDirtyTalk();
                if (_miStop != null && (Playing(_srcMoan) || _now < _ownEnd)) { _miStop.Invoke(_srcMoan, null); _ownEnd = 0f; OwnVoicePlaying = false; }
                _moanT0 = _now; _ownOneShot = false; _packClip = false;
                _pClip.SetValue(_srcMoan, clip, null);
                if (_pPitch != null) _pPitch.SetValue(_srcMoan, pitch, null);
                _pitchHold = true; _pitchSet = pitch;
                _miPlay.Invoke(_srcMoan, null);
                if (_srcVoice != null && !Dead(_srcVoice) && _miStop != null) _miStop.Invoke(_srcVoice, null);
                LastClip = clip; LastClipLength = ClipLen(clip) / Mathf.Max(0.5f, pitch);
                if (trigger && HasAnim) { _anim.SetTrigger(_hMoan); MarkTrigger(); }
                return true;
            }
            catch (Exception e) { LogOnce("moanclip:" + e.GetType().Name, "own moan clip failed: " + e.Message); return false; }
        }

        // True when the moan source plays only the silent padding of a clip (the game pads its moan clips to about 4 s, so isPlaying stays
        // true long after the voice is over): the audible part ends after min(clip length, 1.8 s). Our own breath one-shot is tracked by _ownEnd
        // (the source's clip/time say nothing about a one-shot).
        bool TailSilent()
        {
            if (_srcMoan == null || Dead(_srcMoan) || !Playing(_srcMoan)) return false;
            if (_now < _feelEnd && _feelEnd - _now < 10f) return false;   // a Feeling_enter clip is audible for its whole length
            try
            {
                if (_ownOneShot)
                {
                    if (_now - _ownT0 > 6f || _now < _ownT0) { _ownOneShot = false; return false; }   // stale (a clip of the game's started meanwhile)
                    return _now >= _ownEnd;
                }
                float pitch = Mathf.Max(0.5f, GetPitch(_srcMoan, 1f));
                float len = ClipLen(_pClip != null ? _pClip.GetValue(_srcMoan, null) as UnityEngine.Object : null);
                if (len <= 0f) return false;
                float limit = _packClip ? len / pitch : Mathf.Min(len / pitch, 1.8f), el;   // 0.9.0: a voice pack's clips are not padded
                if (_pTime != null) el = Convert.ToSingle(_pTime.GetValue(_srcMoan, null)) / pitch;
                else el = _now - _moanT0;
                return el >= limit;
            }
            catch (Exception) { return false; }
        }

        // Dirty talk stops the way the game does it when a moan breaks in: FacialFunction.DirtyTalkFacialAnimationStop() (stops the source and
        // the talking face), the subtitle hidden; the raw Stop of the source only when that is not available or left the source playing.
        void StopDirtyTalk()
        {
            if (_srcDirty == null || Dead(_srcDirty) || !Playing(_srcDirty)) return;
            try
            {
                if (_miDtStop != null && _facial != null && !Dead(_facial))
                {
                    _miDtStop.Invoke(_facial, null);
                    if (_miSubHide != null)
                    {
                        var sub = _mSubtitle != null && _mSubtitle.Ok ? _mSubtitle.Get(_facial) : null;
                        if (sub != null && !Dead(sub)) _miSubHide.Invoke(sub, null);
                    }
                }
            }
            catch (Exception e) { LogOnce("dtstop:" + e.GetType().Name, "DirtyTalkFacialAnimationStop failed: " + (e.InnerException ?? e).Message); }
            try { if (Playing(_srcDirty) && _miStop != null) _miStop.Invoke(_srcDirty, null); }
            catch (Exception) { }
        }

        // A strong hit cuts the moan or breath that is still sounding (before the game's MoanFunction starts its own).
        public void StopMoan()
        {
            if (!Found || _srcMoan == null || Dead(_srcMoan) || _miStop == null) return;
            try { _miStop.Invoke(_srcMoan, null); _ownEnd = 0f; _feelEnd = 0f; OwnVoicePlaying = false; MoanPlaying = false; MoanTail = false; _ownOneShot = false; }
            catch (Exception e) { LogOnce("stopmoan", "stopping the moan source failed: " + e.Message); }
        }

        float GetPitch(Behaviour src, float def)
        {
            try { return _pPitch != null ? Convert.ToSingle(_pPitch.GetValue(src, null)) : def; } catch (Exception) { return def; }
        }

        public void RestorePitch()
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

        // ---- 0.9.0: a voice pack (Foley.cs VoicePack) instead of the game's voice library for our reactions ----
        public static readonly VoicePack Pack = new VoicePack();
        bool _packClip;

        // Loads the configured pack when it changed (main thread; the first load waits for the game's AudioClip type).
        public void UsePack(string name)
        {
            name = name ?? "";
            if (name == Pack.Name && (Pack.Loaded || name.Length == 0 || Pack.Summary.Length > 0)) return;
            if (_pClip == null) return;
            Pack.Load(name, _pClip.PropertyType);
        }

        // A pack clip of category c on the moan source, like MoanClip (talking stopped, a sounding moan cut, the voice loop stopped, the
        // "moan" trigger for the mouth). pitch: AudioSource.pitch, restored when it ends.
        public bool PackVoice(int c, float pitch, bool stopDirty, bool trigger = true)
        {
            if (!Found || !Pack.Loaded || _srcMoan == null || Dead(_srcMoan) || _pClip == null || _miPlay == null) return false;
            try
            {
                float len;
                var clip = Pack.Pick(c, out len);
                if (clip == null) return false;
                if (!_pitchHold) _pitchBase = GetPitch(_srcMoan, 1f);
                if (stopDirty) StopDirtyTalk();
                if (_miStop != null && (Playing(_srcMoan) || _now < _ownEnd)) { _miStop.Invoke(_srcMoan, null); _ownEnd = 0f; OwnVoicePlaying = false; }
                _moanT0 = _now; _ownOneShot = false; _packClip = true; _feelEnd = 0f;
                _pClip.SetValue(_srcMoan, clip, null);
                if (_pPitch != null) _pPitch.SetValue(_srcMoan, pitch, null);
                _pitchHold = true; _pitchSet = pitch;
                _miPlay.Invoke(_srcMoan, null);
                if (_srcVoice != null && !Dead(_srcVoice) && _miStop != null) _miStop.Invoke(_srcVoice, null);
                LastClip = clip; LastClipLength = len / Mathf.Max(0.5f, pitch);
                if (trigger && HasAnim) { _anim.SetTrigger(_hMoan); MarkTrigger(); }
                return true;
            }
            catch (Exception e) { LogOnce("packvoice:" + e.GetType().Name, "voice pack clip failed: " + e.Message); return false; }
        }

        // A pack breath (calm or heavy) as a one-shot on the moan source, like Breath().
        public bool PackBreath(bool heavy)
        {
            if (!Found || !Pack.Loaded || _srcMoan == null || Dead(_srcMoan) || _miOneShot == null) return false;
            try
            {
                if (Playing(_srcMoan)) { if (!TailSilent()) return false; StopMoan(); }
                RestorePitch();
                float len;
                var clip = Pack.Pick(heavy ? VoicePack.BreathHeavy : VoicePack.BreathCalm, out len);
                if (clip == null) return false;
                _argOneShot[0] = clip; _argOneShot[1] = BreathVolume * 1.4f;
                _miOneShot.Invoke(_srcMoan, _argOneShot);
                LastClip = clip; LastClipLength = len;
                _ownEnd = _now + len; _ownT0 = _now; _ownOneShot = true;
                OwnVoicePlaying = true;
                return true;
            }
            catch (Exception e) { LogOnce("packbreath:" + e.GetType().Name, "voice pack breath failed: " + e.Message); return false; }
        }

        // A quiet breath (Exhale_slow, else Exhale_normal) as a one-shot on the moan source; no trigger. Skipped while the game's moan sounds.
        public bool Breath()
        {
            if (!Found || !HasBreath || _srcMoan == null || Dead(_srcMoan)) return false;
            try
            {
                if (Playing(_srcMoan))
                {
                    if (!TailSilent()) return false;
                    StopMoan();   // only the silent padding of the last clip is left: free
                }
                RestorePitch();
                var clip = _breath.Pick();
                if (clip == null) return false;
                _argOneShot[0] = clip; _argOneShot[1] = BreathVolume;
                _miOneShot.Invoke(_srcMoan, _argOneShot);
                LastClip = clip; LastClipLength = ClipLen(clip);
                _ownEnd = _now + Mathf.Min(LastClipLength, 1.8f);   // the game's clips are padded: the audible part is over by then
                _ownT0 = _now; _ownOneShot = true;
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
            _sfxSrc = MakeSrc("SfReact_sfx", ref _sfxGo, _sfxSrc);
            return _sfxSrc != null;
        }

        // One of our 3D sources under her root on the game's sound-effect mixer group (the slap one-shots; 0.9.0: the foley one-shots, the rub loop).
        Behaviour MakeSrc(string name, ref GameObject go, Behaviour cur)
        {
            if (cur != null && !Dead(cur)) return cur;
            if (_tSrc == null || _herRoot == null || _srcSfx == null) return null;
            try
            {
                if (go != null) UnityEngine.Object.Destroy(go);
                go = new GameObject(name);
                go.transform.SetParent(_herRoot, false);
                var src = go.AddComponent(_tSrc) as Behaviour;
                if (src == null) { UnityEngine.Object.Destroy(go); go = null; return null; }
                if (_pPlayAwake != null) _pPlayAwake.SetValue(src, false, null);
                if (_pBlend != null) _pBlend.SetValue(src, 0.6f, null);                  // mostly 3D, but not lost at arm's length
                if (_pRolloff != null) _pRolloff.SetValue(src, Enum.ToObject(_pRolloff.PropertyType, 0), null);   // Logarithmic
                if (_pMin != null) _pMin.SetValue(src, 1.5f, null);
                if (_pMax != null) _pMax.SetValue(src, 12f, null);
                if (_pVolume != null) _pVolume.SetValue(src, 1f, null);
                if (_pGroup != null) _pGroup.SetValue(src, _pGroup.GetValue(_srcSfx, null), null);
                return src;
            }
            catch (Exception e) { LogOnce("sfxsrc:" + e.GetType().Name, "own sound source failed: " + e.Message); return null; }
        }

        // A slap clip (hard: sex_fast, soft: sex_normal, else handJob_fast; draw-and-park 4) as a one-shot at `at` with volume scale `vol`
        // (may exceed 1) and source pitch `pitch`. Respects the game's sound-effect setting. Never touches the game's shared source or clip.
        // diag: also log the clip, the listener distance and the mixer level (always done once, on the first slap).
        public bool Slap(Vector3 at, float vol, float pitch, bool hard, bool diag)
        {
            if (!Found || !HasSfx || !SfxOn) return false;
            if (!EnsureSfx()) return false;
            try
            {
                var clip = (hard ? _slapHard : _slapSoft).Pick();
                if (clip == null) return false;
                _sfxGo.transform.position = at;
                if (_pPitch != null) _pPitch.SetValue(_sfxSrc, pitch, null);
                _argOneShot[0] = clip; _argOneShot[1] = Mathf.Max(0f, vol);
                _miOneShot.Invoke(_sfxSrc, _argOneShot);
                LastSlapClip = clip;
                if (diag || !_slapDiag) { _slapDiag = true; SlapDiag(clip, at, vol, hard); }
                return true;
            }
            catch (Exception e) { LogOnce("slap:" + e.GetType().Name, "slap sound failed: " + e.Message); return false; }
        }

        // ---- 0.9.0: skin foley (Foley.cs): the user's own sound files, else built sounds; slaps fall back to the game's clips above ----
        public static readonly SkinFoley Foley = new SkinFoley();
        GameObject _folGo, _rubGo; Behaviour _folSrc, _rubSrc;
        float _rubVol;
        bool _rubOn;

        // The foley is read once, on the main thread, the first time a sound is wanted (the AudioClip type comes from the game's sources).
        public bool FoleyReady()
        {
            if (!Foley.Loaded && !Foley.Failed && _pClip != null) Foley.Load(_pClip.PropertyType);
            return Foley.Loaded;
        }

        // A foley one-shot of category c at `at` (slaps on the slap source; pat / grab on their own, so a slap's pitch never bends a pat).
        // False = no clip for it (a slap then uses the game's clips). Respects the game's sound-effect setting.
        public bool FoleyShot(int c, Vector3 at, float vol, float pitch)
        {
            if (!Found || !SfxOn || _miOneShot == null || !FoleyReady()) return false;
            bool slap = c <= SkinFoley.SlapHard;
            if (slap && !Foley.HasFiles(SkinFoley.SlapSoft) && !Foley.HasFiles(SkinFoley.SlapMedium) && !Foley.HasFiles(SkinFoley.SlapHard)) return false;
            Behaviour src; GameObject go;
            if (slap) { if (!EnsureSfx()) return false; src = _sfxSrc; go = _sfxGo; }
            else { _folSrc = MakeSrc("SfReact_foley", ref _folGo, _folSrc); if (_folSrc == null) return false; src = _folSrc; go = _folGo; }
            try
            {
                float len;
                var clip = Foley.Pick(c, out len);
                if (clip == null) return false;
                go.transform.position = at;
                if (_pPitch != null) _pPitch.SetValue(src, pitch, null);
                _argOneShot[0] = clip; _argOneShot[1] = Mathf.Max(0f, vol);
                _miOneShot.Invoke(src, _argOneShot);
                if (slap) LastSlapClip = clip;
                return true;
            }
            catch (Exception e) { LogOnce("foley:" + e.GetType().Name, "foley sound failed: " + e.Message); return false; }
        }

        // The skin rub loop: level 0..1 (from how fast a hand slides over her), eased (up 6/s, down 3/s), stopped at 0. Call every frame.
        public void Rub(Vector3 at, float level, float pitch, float dt)
        {
            if (!_rubOn && level <= 0f) return;
            if (!Found || !SfxOn) level = 0f;
            _rubVol = Mathf.MoveTowards(_rubVol, level, (level > _rubVol ? 6f : 3f) * dt);
            try
            {
                if (_rubVol <= 0.002f)
                {
                    if (_rubOn && _rubSrc != null && !Dead(_rubSrc) && _miStop != null) _miStop.Invoke(_rubSrc, null);
                    _rubOn = false; _rubVol = 0f;
                    return;
                }
                if (!_rubOn)
                {
                    if (!FoleyReady() || _pClip == null || _miPlay == null) return;
                    _rubSrc = MakeSrc("SfReact_rub", ref _rubGo, _rubSrc);
                    if (_rubSrc == null) return;
                    float len;
                    var clip = Foley.Pick(SkinFoley.Rub, out len);
                    if (clip == null) return;
                    _pClip.SetValue(_rubSrc, clip, null);
                    if (_pLoop != null) _pLoop.SetValue(_rubSrc, true, null);
                    if (_pVolume != null) _pVolume.SetValue(_rubSrc, 0f, null);
                    _miPlay.Invoke(_rubSrc, null);
                    if (_pTime != null && len > 0.2f) _pTime.SetValue(_rubSrc, UnityEngine.Random.Range(0f, len * 0.9f), null);   // not always from the top
                    _rubOn = true;
                }
                if (_rubSrc == null || Dead(_rubSrc)) { _rubOn = false; return; }
                _rubGo.transform.position = at;
                if (_pVolume != null) _pVolume.SetValue(_rubSrc, _rubVol, null);
                if (_pPitch != null) _pPitch.SetValue(_rubSrc, pitch, null);
            }
            catch (Exception e) { LogOnce("rub:" + e.GetType().Name, "rub sound failed: " + e.Message); _rubOn = false; }
        }

        // ---- 0.9.0: her voice's pitch and formant (VoiceShift.cs) on the game's voice sources ----
        readonly System.Collections.Generic.List<VoiceShift> _shift = new System.Collections.Generic.List<VoiceShift>();
        public string VoiceFxStatus = "";

        // Called once a second (also with Reactions off): sets the shared values and puts one filter on each voice source's GameObject while
        // either value is off 1. A GameObject that also carries the game's sound-effect source is skipped (its slaps would shift too).
        public void VoiceFx(float pitch, float formant)
        {
            VoiceShift.Pitch = Mathf.Clamp(pitch, 0.5f, 2f); VoiceShift.Formant = Mathf.Clamp(formant, 0.5f, 2f);
            bool want = Mathf.Abs(pitch - 1f) > 0.004f || Mathf.Abs(formant - 1f) > 0.004f;
            for (int i = _shift.Count - 1; i >= 0; i--) if (_shift[i] == null) _shift.RemoveAt(i);
            if (!want) { VoiceFxStatus = ""; return; }
            if (!Found) { VoiceFxStatus = "her voice sources are not found yet"; return; }
            if (_shift.Count > 0) return;
            try
            {
                if (_tSrc != null)
                {
                    var tSet = _tSrc.Assembly.GetType("UnityEngine.AudioSettings");
                    var pRate = tSet != null ? tSet.GetProperty("outputSampleRate", BindingFlags.Static | BindingFlags.Public) : null;
                    if (pRate != null) VoiceShift.Rate = Mathf.Clamp((int)pRate.GetValue(null, null), 8000, 192000);
                }
                var sfxGo = _srcSfx != null && !Dead(_srcSfx) ? _srcSfx.gameObject : null;
                var sb = new StringBuilder();
                int skipped = 0;
                foreach (var s in new[] { _srcMoan, _srcDirty, _srcVoice, _srcDialogue })
                {
                    if (s == null || Dead(s)) continue;
                    var go = s.gameObject;
                    if (go == sfxGo) { skipped++; continue; }
                    if (go.GetComponent<VoiceShift>() != null) continue;
                    _shift.Add(go.AddComponent<VoiceShift>());
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(go.name);
                }
                VoiceFxStatus = _shift.Count > 0 ? "on " + _shift.Count + " voice source object(s)" : "no voice source to filter";
                Log("voice pitch/formant filter on " + (sb.Length > 0 ? sb.ToString() : "nothing") + " (output " + VoiceShift.Rate + " Hz"
                    + (skipped > 0 ? "; " + skipped + " source(s) skipped: they share the sound-effect source's object" : "") + ")");
            }
            catch (Exception e) { LogOnce("vfx:" + e.GetType().Name, "voice filter failed: " + e.Message); VoiceFxStatus = "failed (see the log)"; }
        }

        // What the slap sounded like on paper: clip name and length, how far the listener is, the mixer group and its level in dB.
        void SlapDiag(UnityEngine.Object clip, Vector3 at, float vol, bool hard)
        {
            try
            {
                var sb = new StringBuilder("slap sound: ");
                sb.Append(hard ? "hard " : "soft ").Append("clip ").Append(clip.name).Append(' ').Append(ClipLen(clip).ToString("0.00")).Append("s, volume scale ").Append(vol.ToString("0.00"));
                if (_tListener == null && _tSrc != null) _tListener = _tSrc.Assembly.GetType("UnityEngine.AudioListener");
                var lis = _tListener != null ? UnityEngine.Object.FindObjectOfType(_tListener) as Component : null;
                sb.Append(", listener ").Append(lis != null ? (lis.transform.position - at).magnitude.ToString("0.00") + " m away (" + lis.name + ")" : "not found");
                string grp = "?";
                var g = _pGroup != null ? _pGroup.GetValue(_sfxSrc, null) as UnityEngine.Object : null;
                grp = g != null ? g.name : "none";
                sb.Append(", mixer group ").Append(grp);
                var mixer = _mMixer != null && _mMixer.Ok && _snd != null ? _mMixer.Get(_snd) : null;
                if (mixer != null)
                {
                    var mi = mixer.GetType().GetMethod("GetFloat", new[] { typeof(string), typeof(float).MakeByRefType() });
                    if (mi != null)
                    {
                        var a = new object[] { "soundEffect", 0f };
                        var r = mi.Invoke(mixer, a);
                        bool ok = r is bool && (bool)r;
                        sb.Append(", game 'soundEffect' level ").Append(ok ? ((float)a[1]).ToString("0.0") + " dB" : "not exposed");
                    }
                }
                else sb.Append(", mixer not reachable");
                Log(sb.ToString());
            }
            catch (Exception e) { LogOnce("slapdiag", "slap diagnostics failed: " + e.Message); }
        }

        // ======================================================================================================================
        // Diagnose (the panel's button): one pass over ~3 s that logs what the audio and face chain looks like and plays a test tone, one of the
        // game's slap clips and the "moan" trigger. A small state machine ticked from Reactions.Update (one step per tick, DiagAt = seconds from
        // the start); everything is collected into one multi-line log entry "Reactions diagnose:" written at the end. Not used by anything else.
        // ======================================================================================================================
        public bool DiagBusy { get { return _dActive; } }
        public bool DiagRan;
        bool _dActive, _dSaved, _dTrig;
        int _dStep; float _dT0;
        StringBuilder _dSb;
        Component _dListener;
        Type _tLis;
        UnityEngine.Object _dTone;
        float _dBlend0, _dVol0, _dPitch0; object _dGroup0;
        int[] _dHash; float[] _dWt;
        static readonly float[] DiagAt = { 0f, 0.1f, 0.5f, 1.0f, 1.1f, 1.5f, 1.6f, 2.0f, 2.3f, 3.0f };

        public void DiagStart(float now)
        {
            if (!Found || _dActive) return;
            _dActive = true; _dStep = 0; _dT0 = now; _dTrig = false; _dSaved = false; _dListener = null; _dHash = null; _dWt = null;
            _dSb = new StringBuilder("Reactions diagnose: start (diagnose)");
        }

        // Every frame; runs the next step when its time has come. The game link lost: the run ends where it is.
        public void DiagTick(float now)
        {
            if (!_dActive) return;
            if (!Found) { DiagEnd("aborted: the game link was lost"); return; }
            float t = now - _dT0;
            if (t < 0f) { _dT0 = now; t = 0f; }
            if (_dStep < DiagAt.Length && t >= DiagAt[_dStep])
            {
                int s = _dStep++;
                try { DiagStep(s); }
                catch (Exception e) { D("step " + s + " failed: " + (e.InnerException ?? e).GetType().Name + " " + (e.InnerException ?? e).Message); }
            }
            if (_dStep >= DiagAt.Length) DiagEnd(null);
        }

        void D(string line) { if (_dSb != null) _dSb.Append("\n  (diagnose) ").Append(line); }

        void DiagEnd(string why)
        {
            if (!_dActive) return;
            _dActive = false;
            try { DiagRestoreSfx(); } catch (Exception) { }
            try { if (_dTrig && _anim != null && !Dead(_anim) && HasAnim) _anim.ResetTrigger(_hMoan); } catch (Exception) { }
            _dTrig = false;
            try { if (!ReferenceEquals(_dTone, null)) UnityEngine.Object.Destroy(_dTone); } catch (Exception) { }
            _dTone = null; _dListener = null;
            D(why ?? "done (test tone removed)");
            DiagRan = true;
            if (_dSb != null) Plugin.Log.LogInfo(_dSb.ToString());
            _dSb = null;
        }

        static object Gp(object o, string name)
        {
            if (o == null) return null;
            try
            {
                var t = o.GetType();
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public);
                if (f != null) return f.GetValue(o);
            }
            catch (Exception) { }
            return null;
        }

        static object Sp(Type t, string name)
        {
            if (t == null) return null;
            try
            {
                var p = t.GetProperty(name, BindingFlags.Static | BindingFlags.Public);
                if (p != null && p.CanRead) return p.GetValue(null, null);
            }
            catch (Exception) { }
            return null;
        }

        static string Sv(object v)
        {
            if (v == null) return "?";
            if (v is float) return ((float)v).ToString("0.###");
            if (v is bool) return (bool)v ? "yes" : "no";
            var u = v as UnityEngine.Object;
            if (!ReferenceEquals(u, null)) return u != null ? u.name : "(destroyed)";
            return v.ToString();
        }

        static string Yn(bool b) { return b ? "yes" : "no"; }

        string GroupOf(Behaviour src)
        {
            try { var g = _pGroup != null && src != null ? _pGroup.GetValue(src, null) as UnityEngine.Object : null; return g != null ? g.name : "none"; }
            catch (Exception) { return "?"; }
        }

        string MixerOf(Behaviour src)
        {
            try { var g = _pGroup != null && src != null ? _pGroup.GetValue(src, null) as UnityEngine.Object : null; return g != null ? Sv(Gp(g, "audioMixer")) : "none"; }
            catch (Exception) { return "?"; }
        }

        string LoadState(UnityEngine.Object clip) { return clip != null ? Sv(Gp(clip, "loadState")) : "no clip"; }

        string ClipText(UnityEngine.Object clip)
        {
            if (clip == null) return "none";
            return clip.name + " (" + ClipLen(clip).ToString("0.00") + " s, " + Sv(Gp(clip, "frequency")) + " Hz, " + Sv(Gp(clip, "channels")) + " ch, loadState " + LoadState(clip)
                + ", preloadAudioData " + Sv(Gp(clip, "preloadAudioData")) + ", loadInBackground " + Sv(Gp(clip, "loadInBackground")) + ")";
        }

        Vector3 DiagFront(float d)
        {
            if (_dListener != null && !Dead(_dListener)) return _dListener.transform.position + _dListener.transform.forward * d;
            return _herRoot != null ? _herRoot.position + Vector3.up : Vector3.zero;
        }

        void DiagStep(int s)
        {
            switch (s)
            {
                case 0:
                    D("Time.timeScale " + Time.timeScale.ToString("0.###") + ", voice language " + Language);
                    DiagListeners();
                    DiagOurSource();
                    DiagGameSources();
                    DiagTone(1);
                    break;
                case 1:
                    D("tone 1 (our source, 2D, no mixer group, volume 1) 0.1 s after Play: isPlaying " + Yn(Playing(_sfxSrc)) + ", source time " + Sv(Gp(_sfxSrc, "time"))
                      + ", clip loadState " + LoadState(_dTone));
                    break;
                case 2:
                    DiagRestoreSfx();
                    break;
                case 3:
                    DiagTone(2);
                    break;
                case 4:
                    D("tone 2 (game's sfx mixer group, 3D) 0.1 s after Play: isPlaying " + Yn(Playing(_sfxSrc)) + ", source time " + Sv(Gp(_sfxSrc, "time"))
                      + ", clip loadState " + LoadState(_dTone));
                    break;
                case 5:
                    DiagRestoreSfx();
                    DiagSlap();
                    break;
                case 6:
                    D("slap clip 0.1 s after PlayOneShot: our source isPlaying " + Yn(Playing(_sfxSrc)) + ", clip loadState " + LoadState(_dSlap));
                    break;
                case 7:
                    DiagFace(true);
                    break;
                case 8:
                    DiagFace(false);
                    break;
            }
        }

        // (a) listeners and global audio settings
        void DiagListeners()
        {
            if (_tLis == null && _tSrc != null) _tLis = _tSrc.Assembly.GetType("UnityEngine.AudioListener");
            if (_tLis == null) { D("AudioListener type not found"); return; }
            var all = UnityEngine.Object.FindObjectsOfType(_tLis);
            int enabled = 0;
            var names = new StringBuilder();
            for (int i = 0; i < all.Length; i++)
            {
                var b = all[i] as Behaviour;
                if (b == null) continue;
                bool en = b.enabled && b.gameObject.activeInHierarchy;
                if (en) { enabled++; if (_dListener == null) _dListener = b; }
                if (names.Length > 0) names.Append("; ");
                var p = b.transform.position;
                names.Append(b.gameObject.name).Append(en ? "" : " (disabled)").Append(" at ").Append(p.x.ToString("0.00")).Append(',').Append(p.y.ToString("0.00")).Append(',').Append(p.z.ToString("0.00"));
            }
            D("AudioListener: " + all.Length + " found, " + enabled + " enabled" + (names.Length > 0 ? " [" + names + "]" : "") + (enabled == 0 ? " (NO enabled listener: Unity plays nothing)" : enabled > 1 ? " (more than one enabled listener)" : ""));
            D("AudioListener.pause " + Sv(Sp(_tLis, "pause")) + ", AudioListener.volume " + Sv(Sp(_tLis, "volume")));
            var tAs = _tSrc.Assembly.GetType("UnityEngine.AudioSettings");
            if (tAs == null) { D("AudioSettings type not found"); return; }
            var sb = new StringBuilder("AudioSettings: outputSampleRate " + Sv(Sp(tAs, "outputSampleRate")) + ", speakerMode " + Sv(Sp(tAs, "speakerMode")));
            try
            {
                var mc = tAs.GetMethod("GetConfiguration", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
                var cfg = mc != null ? mc.Invoke(null, null) : null;
                if (cfg != null)
                {
                    sb.Append(", configuration {");
                    var fs = cfg.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public);
                    for (int i = 0; i < fs.Length; i++) sb.Append(i > 0 ? ", " : "").Append(fs[i].Name).Append(' ').Append(Sv(fs[i].GetValue(cfg)));
                    sb.Append('}');
                }
            }
            catch (Exception) { }
            D(sb.ToString());
        }

        // (b) our slap source
        void DiagOurSource()
        {
            if (!EnsureSfx()) { D("our sfx source: could NOT be created (" + (_tSrc == null ? "no AudioSource type" : _herRoot == null ? "no root" : _srcSfx == null ? "the game's sfx source is missing" : "AddComponent failed") + ")"); return; }
            DiagSaveSfx();
            string dist = "no listener";
            if (_dListener != null && !Dead(_dListener)) dist = (_sfxSrc.transform.position - _dListener.transform.position).magnitude.ToString("0.00") + " m";
            D("our sfx source: exists yes, activeInHierarchy " + Yn(_sfxSrc.gameObject.activeInHierarchy) + ", enabled " + Yn(_sfxSrc.enabled) + ", mute " + Sv(Gp(_sfxSrc, "mute"))
              + ", volume " + Sv(Gp(_sfxSrc, "volume")) + ", spatialBlend " + Sv(Gp(_sfxSrc, "spatialBlend")) + ", minDistance " + Sv(Gp(_sfxSrc, "minDistance")) + ", maxDistance " + Sv(Gp(_sfxSrc, "maxDistance"))
              + ", mixer group " + GroupOf(_sfxSrc) + ", mixer " + MixerOf(_sfxSrc) + ", distance to the listener (where the last slap was) " + dist + ", under " + _sfxGo.transform.parent.name);
        }

        // (c) the game's sources and the mixer
        void DiagGameSources()
        {
            string[] nm = { "moan", "dirtyTalk", "sfx", "voice", "dialogue" };
            Behaviour[] sr = { _srcMoan, _srcDirty, _srcSfx, _srcVoice, _srcDialogue };
            for (int i = 0; i < sr.Length; i++)
            {
                var b = sr[i];
                if (b == null || Dead(b)) { D("game source " + nm[i] + ": not found"); continue; }
                UnityEngine.Object clip = null;
                try { clip = _pClip != null ? _pClip.GetValue(b, null) as UnityEngine.Object : null; } catch (Exception) { }
                D("game source " + nm[i] + ": enabled " + Yn(b.enabled) + ", activeInHierarchy " + Yn(b.gameObject.activeInHierarchy) + ", mute " + Sv(Gp(b, "mute")) + ", volume " + Sv(Gp(b, "volume"))
                  + ", spatialBlend " + Sv(Gp(b, "spatialBlend")) + ", isPlaying " + Yn(Playing(b)) + ", clip " + (clip != null ? clip.name : "none") + ", mixer group " + GroupOf(b) + ", mixer " + MixerOf(b));
            }
            object mixer = null;
            try
            {
                var g = _pGroup != null && _srcSfx != null ? _pGroup.GetValue(_srcSfx, null) : null;
                mixer = g != null ? Gp(g, "audioMixer") : null;
                if (mixer == null && _mMixer != null && _mMixer.Ok && _snd != null) mixer = _mMixer.Get(_snd);
            }
            catch (Exception) { }
            if (mixer == null) { D("AudioMixer of the game's sfx source: not reachable"); return; }
            var mi = mixer.GetType().GetMethod("GetFloat", new[] { typeof(string), typeof(float).MakeByRefType() });
            if (mi == null) { D("AudioMixer " + Sv(mixer) + ": GetFloat not found"); return; }
            var sb = new StringBuilder("AudioMixer " + Sv(mixer) + " exposed levels (dB): ");
            string[] names = { "soundEffect", "voice", "moan", "Master", "master", "BGM", "bgm", "dirtyTalk", "system", "environment" };
            for (int i = 0; i < names.Length; i++)
            {
                var a = new object[] { names[i], 0f };
                var r = mi.Invoke(mixer, a);
                sb.Append(i > 0 ? ", " : "").Append(names[i]).Append(' ').Append(r is bool && (bool)r ? ((float)a[1]).ToString("0.0") : "not exposed");
            }
            D(sb.ToString());
        }

        void DiagSaveSfx()
        {
            if (_dSaved || _sfxSrc == null || Dead(_sfxSrc)) return;
            _dBlend0 = Convert.ToSingle(Gp(_sfxSrc, "spatialBlend") ?? 0.6f); _dVol0 = Convert.ToSingle(Gp(_sfxSrc, "volume") ?? 1f); _dPitch0 = Convert.ToSingle(Gp(_sfxSrc, "pitch") ?? 1f);
            _dGroup0 = _pGroup != null ? _pGroup.GetValue(_sfxSrc, null) : null;
            _dSaved = true;
        }

        // Our source stops and gets back what it had before the probe played on it.
        void DiagRestoreSfx()
        {
            if (!_dSaved) return;
            _dSaved = false;
            if (_sfxSrc == null || Dead(_sfxSrc)) return;
            if (_miStop != null) _miStop.Invoke(_sfxSrc, null);
            if (_pClip != null) _pClip.SetValue(_sfxSrc, null, null);
            if (_pBlend != null) _pBlend.SetValue(_sfxSrc, _dBlend0, null);
            if (_pVolume != null) _pVolume.SetValue(_sfxSrc, _dVol0, null);
            if (_pPitch != null) _pPitch.SetValue(_sfxSrc, _dPitch0, null);
            if (_pGroup != null) _pGroup.SetValue(_sfxSrc, _dGroup0, null);
        }

        // (d) the test tone: 0.4 s, 660 Hz, mono, built here. which 1: on our source as 2D without a mixer group at volume 1; 2: through the game's
        // sfx mixer group, 3D, 0.5 m in front of the listener.
        void DiagTone(int which)
        {
            if (!EnsureSfx() || _pClip == null || _miPlay == null) { D("test tone " + which + ": no sound source to play on"); return; }
            DiagSaveSfx();
            if (_dTone == null)
            {
                const int rate = 44100, n = 17640, fade = 441;
                var data = new float[n];
                for (int i = 0; i < n; i++)
                {
                    float env = i < fade ? i / (float)fade : i >= n - fade ? (n - 1 - i) / (float)fade : 1f;
                    data[i] = 0.5f * Mathf.Sin(2f * Mathf.PI * 660f * i / rate) * env;
                }
                var tClip = _pClip.PropertyType;
                var create = tClip.GetMethod("Create", new[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(bool) });
                var setData = tClip.GetMethod("SetData", new[] { typeof(float[]), typeof(int) });
                if (create == null || setData == null) { D("test tone: AudioClip.Create / SetData not found"); return; }
                _dTone = create.Invoke(null, new object[] { "SfDiagTone", n, 1, rate, false }) as UnityEngine.Object;
                if (_dTone == null) { D("test tone: AudioClip.Create returned nothing"); return; }
                object ok = setData.Invoke(_dTone, new object[] { data, 0 });
                D("test tone created: SfDiagTone, " + n + " samples at " + rate + " Hz (0.4 s, 660 Hz, mono, not streaming), SetData " + Sv(ok) + ", loadState " + LoadState(_dTone));
            }
            if (which == 1)
            {
                if (_pBlend != null) _pBlend.SetValue(_sfxSrc, 0f, null);
                if (_pGroup != null) _pGroup.SetValue(_sfxSrc, null, null);
                _sfxGo.transform.localPosition = Vector3.zero;
            }
            else
            {
                var grp = _pGroup != null && _srcSfx != null ? _pGroup.GetValue(_srcSfx, null) : null;
                if (_pBlend != null) _pBlend.SetValue(_sfxSrc, 1f, null);
                if (_pGroup != null) _pGroup.SetValue(_sfxSrc, grp, null);
                _sfxGo.transform.position = DiagFront(0.5f);
            }
            if (_pVolume != null) _pVolume.SetValue(_sfxSrc, 1f, null);
            if (_pPitch != null) _pPitch.SetValue(_sfxSrc, 1f, null);
            _pClip.SetValue(_sfxSrc, _dTone, null);
            _miPlay.Invoke(_sfxSrc, null);
            D("tone " + which + " played: " + (which == 1 ? "on our source, 2D (spatialBlend 0), no mixer group, volume 1, at her root" : "on our source through the game's sfx mixer group '" + GroupOf(_srcSfx) + "', 3D, volume 1, 0.5 m in front of the listener"));
        }

        UnityEngine.Object _dSlap;

        // (e) one game slap clip on our source the way the slap path plays it
        void DiagSlap()
        {
            if (!EnsureSfx() || _miOneShot == null) { D("slap clip: no sound source to play on"); return; }
            DiagSaveSfx();
            var clip = (_slapHard.Count > 0 ? _slapHard : _slapSoft).Pick();
            _dSlap = clip;
            if (clip == null) { D("slap clip: none in the library (HasSfx " + Yn(HasSfx) + ")"); return; }
            string before = ClipText(clip);
            _sfxGo.transform.position = DiagFront(0.5f);
            if (_pPitch != null) _pPitch.SetValue(_sfxSrc, 1f, null);
            _argOneShot[0] = clip; _argOneShot[1] = 1f;
            _miOneShot.Invoke(_sfxSrc, _argOneShot);
            D("slap clip played with PlayOneShot(clip, 1) on our source (0.5 m in front of the listener, spatialBlend " + Sv(Gp(_sfxSrc, "spatialBlend")) + ", group " + GroupOf(_sfxSrc) + "): before " + before + "; right after loadState " + LoadState(clip));
        }

        // (f) the face: layers, flags and what "moan" changes. before: first snapshot and the trigger; else the changes 0.3 s later and the reset.
        void DiagFace(bool before)
        {
            if (before)
            {
                D("face flags: facialLayer '" + FacialLayer + "', functionPause " + Yn(FunctionPause) + ", sexMode " + (_mSexMode != null && _mSexMode.Ok ? Sv(Peek(_mSexMode, _sm)) : "?") + ", foreplayMode " + (_mForeplay != null && _mForeplay.Ok ? Sv(Peek(_mForeplay, _sm)) : "?")
                  + ", dialoguePlaying " + Yn(DialoguePlaying) + ", dirtyTalkPause " + Yn(DirtyTalkPause) + ", voiceIsOn " + Yn(VoiceOn) + ", HasAnim(moan) " + Yn(HasAnim));
            }
            if (_anim == null || Dead(_anim)) { D("Animator: not available"); return; }
            if (before)
            {
                D("Animator " + _anim.gameObject.name + ": controller " + Sv(_anim.runtimeAnimatorController) + ", enabled " + Yn(_anim.enabled) + ", speed " + _anim.speed.ToString("0.##") + ", layers " + _anim.layerCount);
            }
            int ln = _anim.layerCount, changed = 0;
            if (before) { _dHash = new int[ln]; _dWt = new float[ln]; }
            for (int i = 0; i < ln; i++)
            {
                var st = _anim.GetCurrentAnimatorStateInfo(i);
                float w = _anim.GetLayerWeight(i);
                if (before)
                {
                    _dHash[i] = st.fullPathHash; _dWt[i] = w;
                    D("layer " + i + " '" + _anim.GetLayerName(i) + "': weight " + w.ToString("0.##") + ", state " + st.fullPathHash + ", normalizedTime " + st.normalizedTime.ToString("0.00") + (_anim.IsInTransition(i) ? ", in transition" : ""));
                }
                else if (_dHash != null && i < _dHash.Length && (_dHash[i] != st.fullPathHash || Mathf.Abs(_dWt[i] - w) > 0.01f))
                {
                    changed++;
                    D("layer " + i + " '" + _anim.GetLayerName(i) + "' CHANGED by the trigger: state " + _dHash[i] + " -> " + st.fullPathHash + ", weight " + _dWt[i].ToString("0.##") + " -> " + w.ToString("0.##"));
                }
            }
            if (before)
            {
                if (HasAnim) { _anim.SetTrigger(_hMoan); _dTrig = true; D("Animator trigger 'moan' set"); }
                else D("Animator has no 'moan' trigger: not set");
            }
            else
            {
                if (changed == 0) D("0.3 s after SetTrigger('moan'): no layer changed its state or weight");
                if (_dTrig) { _anim.ResetTrigger(_hMoan); _dTrig = false; D("Animator trigger 'moan' reset"); }
            }
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
                    float tgt = Mathf.Min(Mathf.Max(b.Base, BlushRange), b.Base + add);
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
        // The voice guard (LadyVoice_Function.PlayRandomVoice IndexOutOfRange / NullReference): keep dirtyTalkVoice / dirtyTalkPlayNum valid for
        // the current dirtyTalkVoiceList, and sexSoundEffectVoice / sexSeNum for the sex sound-effect list (GuardSfx below). Independent of the reactions toggle; never writes randomSequence or dirtyTalkActive.
        // ======================================================================================================================
        void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception || condition == null || stackTrace == null) return;
            if (condition.IndexOf("IndexOutOfRange", StringComparison.Ordinal) < 0 && condition.IndexOf("NullReference", StringComparison.Ordinal) < 0) return;
            if (stackTrace.IndexOf("PlayRandomVoice", StringComparison.Ordinal) < 0) return;
            if (stackTrace.IndexOf("SexSoundEffect", StringComparison.Ordinal) >= 0) { _sfxErr = true; _sfxErrTotal++; return; }   // the sex SFX shuffle, not dirty talk
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
                SetGuardStatus("off");
                return;
            }
            if (!_guardSub) { try { Application.logMessageReceived += _logCb; _guardSub = true; } catch (Exception e) { LogOnce("guardsub", "voice guard: log hook failed: " + e.Message); } }
            if (!Found) { SetGuardStatus("waiting for the game"); return; }
            try
            {
                int ms = Ms();
                if (_guardBackoff)
                {
                    if (unchecked(ms - _backoffUntilMs) < 0) return;
                    _guardBackoff = false; _repaired = false; Interlocked0();
                    _gsVer = -1;
                }
                if (_repaired && System.Threading.Volatile.Read(ref _errAfter) > 20 && unchecked(ms - _lastRepairMs) < 2000)
                {
                    _guardBackoff = true; _backoffUntilMs = unchecked(ms + 60000);
                    SetGuardStatus("repairs don't stop the error, left alone for 60 s");
                    Log("voice guard: repairs don't stop the error (" + _errAfter + " exceptions within 2 s of a repair), leaving it for 60 s. " + GuardDump("state"));
                    return;
                }
                bool err = _voiceErr, serr = _sfxErr;
                if (!err && !serr && unchecked(ms - _guardMs) < 200) return;
                _guardMs = ms; _voiceErr = false; _sfxErr = false;
                GuardTick(err, serr, ms);
            }
            catch (Exception e) { LogOnce("guarderr:" + e.GetType().Name, "voice guard failed: " + e.Message); }
        }

        void Interlocked0() { System.Threading.Interlocked.Exchange(ref _errAfter, 0); }

        void GuardTick(bool err, bool serr, int ms)
        {
            var lv = _lv;
            if (lv == null || Dead(lv))
            {
                // not there (yet): not a verdict. The 2 s tick re-resolves when the object appears.
                SetGuardStatus("waiting for the game's voice object");
                return;
            }
            if (!_dtDead) GuardDirty(err, ms, lv);
            GuardSfx(serr, ms, lv);
            // the shown text is rebuilt only when the dirty-talk part or the sound-effect repair count changed
            if (_gsVer != _dtVer || _gsSfx != _sfxRepairs)
            {
                _gsVer = _dtVer; _gsSfx = _sfxRepairs;
                GuardStatus = _sfxRepairs > 0 && !_dtStatus.StartsWith("off") ? _dtStatus + "; sound-effect shuffle repaired " + _sfxRepairs + " times" : _dtStatus;
            }
        }

        // A status that does not come from the dirty-talk part (waiting, off, backoff): shown as it is until the next change of the real one.
        void SetGuardStatus(string s) { GuardStatus = s; _gsVer = -1; }

        // The dirty-talk part's text, built by the caller only when this says its inputs (case, count) changed.
        bool DtChanged(int code, int n)
        {
            int key = code * 100003 + n;
            if (key == _dtStKey) return false;
            _dtStKey = key; _dtVer++;
            return true;
        }

        // ---- the dirty-talk shuffle ----
        void GuardDirty(bool err, int ms, object lv)
        {
            if (!_mDtActive.Ok || !_mDtList.Ok || !_mDtVoice.Ok || !_mDtNum.Ok || !_mRandSeq.Ok)
            {
                _dtDead = true; _dtStatus = "off: the game's dirty-talk members were not found"; _dtVer++;
                LogOnce("guarddead", "voice guard: off (dirty-talk members missing: " + Missing + ")");
                return;
            }
            int R = Int(_mRandSeq, lv, 0);
            if (R < 1 || R > 64)
            {
                _dtDead = true; _dtStatus = "off: randomSequence is " + R; _dtVer++;
                LogOnce("guardR", "voice guard: off (randomSequence " + R + " is outside 1..64)");
                return;
            }
            if (!Bool(_mDtActive, lv)) { if (DtChanged(1, _repairs)) _dtStatus = _repairs == 0 ? "watching (dirty talk idle)" : "watching, repaired " + _repairs + " times"; return; }
            var list = _mDtList.Get(lv) as Array;
            int len = list != null ? list.Length : 0;
            int n = Int(_mDtNum, lv, -1);
            var seq = _mDtVoice.Get(lv) as int[];
            if (len == 0)
            {
                // an empty list throws in the shuffle every time the timer fires: never let it fire
                if (_mDtSpeed.Ok && Flt(_mDtSpeed, lv, 3f) < 1f) _mDtSpeed.Set(lv, 3f);
                LogOnce("guardempty", "voice guard: the dirty-talk list is empty while active, holding its timer (type " + (_mDtType.Get(lv) as string) + ")");
                if (DtChanged(2, _repairs)) _dtStatus = "dirty-talk list empty, timer held";
                return;
            }
            string why = null;
            if (n >= 0 && (seq == null || n >= seq.Length || seq[n] < 0 || seq[n] >= len)) why = "stale shuffle (index " + n + (seq != null && n < seq.Length ? " -> " + seq[n] : "") + " of a list of " + len + ")";
            else if (n < 0 && len < R) why = "list of " + len + " is shorter than the shuffle window " + R;
            if (why == null)
            {
                if (DtChanged(3, _repairs)) _dtStatus = _repairs == 0 ? "watching" : "watching, repaired " + _repairs + " times";
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
            DtChanged(4, _repairs); _dtVer++; _dtStatus = "repaired " + _repairs + " times (last: " + why + ")";
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

        // ---- the sex sound-effect shuffle ----
        // LadyVoice_Function.SexSoundEffect(speed) plays a list of SoundEffect_Library chosen by soundEffectType and the speed (< 1 slow,
        // 1..1.4 normal, above fast) through the same PlayRandomVoice, with the permutation sexSoundEffectVoice and the counter sexSeNum
        // (cum: cum_soundEffect with cumSoundEffectVoice / cumSeNum). One permutation serves all three speeds, whose lists differ in length;
        // the game's counter also starts at 0 with no permutation. Repairs only what throws for sure (a permutation index at or past every list,
        // no permutation at all, a counter past the permutation, a refill window longer than every list); after an exception from this path
        // it is stricter (the shortest list). A null or empty list cannot be repaired, it is only reported once.
        static string SfxPrefix(string type)
        {
            switch (type)
            {
                case "sex": return "sex_";
                case "handJob": case "footJob": return "handJob_";
                case "tittyFuck": return "tittyFuck_";
                case "finger": return "foreplay_finger_";
                case "sexToy": return "foreplay_sexToy_";
                case "tongue": return "foreplay_tongue_";
                case "sixNine": return "foreplay_sixNine_";
                case "penisMassage": return "foreplay_penisMassage_";
                case "urethraMassage": return "foreplay_urethraMassage_";
            }
            return null;
        }

        Mem[] SfxLists(Type libType, string type)
        {
            Mem[] r;
            if (_seMems.TryGetValue(type, out r)) return r;
            if (type == "cum") r = new[] { Q(libType, "cum_soundEffect") };
            else
            {
                string pre = SfxPrefix(type);
                r = pre == null ? null : new[] { Q(libType, pre + "slow"), Q(libType, pre + "normal"), Q(libType, pre + "fast") };
            }
            _seMems[type] = r;
            return r;
        }

        void GuardSfx(bool err, int ms, object lv)
        {
            if (!_mSeType.Ok || !_mSeVoice.Ok || !_mSeNum.Ok || !_mSfxLib.Ok || !_mRandSeq.Ok) return;
            int R = Int(_mRandSeq, lv, 0);
            if (R < 1 || R > 64) return;
            string type = _mSeType.Get(lv) as string;
            if (string.IsNullOrEmpty(type)) return;
            object lib = _mSfxLib.Get(lv);
            if (lib == null || Dead(lib)) return;
            var lists = SfxLists(lib.GetType(), type);
            if (lists == null) return;                      // a type the game plays nothing for
            bool cum = type == "cum";
            int minLen = int.MaxValue, maxLen = 0, bad = 0;
            for (int i = 0; i < lists.Length; i++)
            {
                if (lists[i] == null || !lists[i].Ok) continue;
                var a = lists[i].Get(lib) as Array;
                int len = a != null ? a.Length : 0;
                if (len == 0) { bad++; continue; }
                if (len < minLen) minLen = len;
                if (len > maxLen) maxLen = len;
            }
            if (bad > 0 && err) LogOnce("sfxnull:" + type, "voice guard: " + bad + " of the sound-effect lists for type " + type + " are null or empty, the game throws whenever it plays those (not repairable)");
            if (maxLen == 0) return;
            Mem mv = cum ? _mCumVoice : _mSeVoice, mn = cum ? _mCumNum : _mSeNum;
            if (!mv.Ok || !mn.Ok) return;
            int n = Int(mn, lv, -1);
            var seq = mv.Get(lv) as int[];
            int lim = err ? minLen : maxLen;                // an index at or past lim throws for sure (maxLen) or for some speed (minLen)
            string why = null;
            if (n >= 0)
            {
                if (seq == null) why = "no sound-effect shuffle (playNum " + n + ")";
                else if (n >= seq.Length) why = "sound-effect playNum " + n + " beyond a shuffle of " + seq.Length;
                else for (int i = 0; i <= n; i++) if (seq[i] < 0 || seq[i] >= lim) { why = "stale sound-effect shuffle (index " + i + " -> " + seq[i] + " of a list of " + lim + ")"; break; }
            }
            else if (R > lim) why = "sound-effect list of " + lim + " is shorter than the shuffle window " + R;
            if (why == null)
            {
                if (err) LogOnce("sfxvalid", "voice guard: PlayRandomVoice threw from the sound effects but the shuffle looks valid. " + SfxDump(lv, type, lists, lib, R, n, seq, why));
                return;
            }
            string dump = !_sfxLoggedFirst ? SfxDump(lv, type, lists, lib, R, n, seq, why) : null;
            if (n >= 0 && minLen >= R) mn.Set(lv, -1);   // the counter says "refill": the game rebuilds it for the list it plays
            else
            {
                // the window is longer than a list: a shuffle of our own over the shortest list, valid for every speed
                var perm = new int[minLen];
                for (int i = 0; i < minLen; i++) perm[i] = i;
                var sq = new int[R];
                int k = minLen;
                for (int i = 0; i < R; i++)
                {
                    if (k >= minLen) { for (int j = minLen - 1; j > 0; j--) { int r = UnityEngine.Random.Range(0, j + 1); int t = perm[j]; perm[j] = perm[r]; perm[r] = t; } k = 0; }
                    sq[i] = perm[k++];
                }
                mv.Set(lv, sq); mn.Set(lv, R - 1);
            }
            _sfxRepairs++; _sfxSinceLog++;
            if (dump != null) { _sfxLoggedFirst = true; _sfxLogMs = ms; _sfxSinceLog = 0; Log("voice guard: " + why + ", repaired. " + dump); }
            else if (unchecked(ms - _sfxLogMs) >= 10000) { _sfxLogMs = ms; Log("voice guard: " + _sfxSinceLog + " more sound-effect repair(s) in the last 10 s (last: " + why + "), " + _sfxErrTotal + " sound-effect exceptions seen in all"); _sfxSinceLog = 0; }
        }

        string SfxDump(object lv, string type, Mem[] lists, object lib, int R, int n, int[] seq, string why)
        {
            try
            {
                var sb = new StringBuilder("soundEffectType ").Append(type).Append(", lists");
                for (int i = 0; i < lists.Length; i++)
                {
                    var a = lists[i] != null && lists[i].Ok ? lists[i].Get(lib) as Array : null;
                    sb.Append(i == 0 ? " " : "/").Append(a != null ? a.Length.ToString() : "null");
                }
                sb.Append(", randomSequence ").Append(R).Append(", playNum ").Append(n).Append(", seq ").Append(seq == null ? "null" : seq.Length + (n >= 0 && n < seq.Length ? " [n]=" + seq[n] : ""))
                  .Append(" (").Append(why ?? "state").Append(')');
                return sb.ToString();
            }
            catch (Exception) { return why ?? "state"; }
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
            if (_eyeOutAt > 0f) { FinishEye(); _eyeResetAt = 0f; try { if (_anim != null && !Dead(_anim) && _hasEyeTrig) { _anim.ResetTrigger(_hEyeIn); _anim.ResetTrigger(_hEyeOut); } } catch (Exception) { } }
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
            if (_dActive) DiagEnd("aborted: the bridge was released");
            ReleaseHeld();
            if (_guardSub) { try { Application.logMessageReceived -= _logCb; } catch (Exception) { } _guardSub = false; }
            try { if (_sfxGo != null) UnityEngine.Object.Destroy(_sfxGo); } catch (Exception) { }
            try { if (_folGo != null) UnityEngine.Object.Destroy(_folGo); if (_rubGo != null) UnityEngine.Object.Destroy(_rubGo); } catch (Exception) { }
            foreach (var v in _shift) try { if (v != null) UnityEngine.Object.Destroy(v); } catch (Exception) { }
            _shift.Clear(); VoiceFxStatus = "";
            _sfxGo = null; _sfxSrc = null; _folGo = null; _folSrc = null; _rubGo = null; _rubSrc = null; _rubOn = false; _rubVol = 0f;
            Found = false; Missing = ""; _missing.Clear();
            _facial = _lv = _snd = _gs = _bp = _sm = _gauge = _solver = null;
            _anim = null; _herRoot = null;
            _srcMoan = _srcDirty = _srcDialogue = _srcSfx = _srcVoice = null; _tSrc = null;
            _isPlayingFast = null; _gazeGet = null; _gazeSet = null; _mGazeW = null;
            _miMoan = _miBlink = _miDesirePlus = _miDtStop = _miSubHide = null; _mSubtitle = null;
            for (int i = 0; i < _moanBanks.Length; i++) _moanBanks[i].Clips = null;
            _breath.Clips = null; _slapHard.Clips = null; _slapSoft.Clips = null; _bankLang = -1;
            for (int i = 0; i < _lines.Length; i++) _lines[i].Clips = null;
            for (int i = 0; i < _feel.Length; i++) _feel[i] = null;
            HasLines = HasFeeling = LinePathOk = DirtyTalkOn = false; _lineLang = -1; _lineFails = 0; _feelEnd = 0f;
            _miDtSelect = _miDtPlay = null; LastLineClip = null; LastLineLength = 0f; LineHow = "";
            _eyeOutAt = 0f; _eyeResetAt = 0f; _eyeIds[0] = _eyeIds[1] = _eyeIds[2] = null; HasEyeReact = false; _hasEyeTrig = false; _eyeText = "";
            _miEyeIn = _miEyeOut = null; _mMixer = null; _slapDiag = false; _blushSig = ""; _gateMask = 0; _gateLogT = -100f;
            DialogueWhy = ""; SexFacialPlaying = false; SlapBanks = "";
            _bm.Clear(); _bmSrc.Clear(); _bmFirst.Clear(); BlushFaceCount = BlushBodyCount = 0; _rangeCache.Clear();
            _hasMoanFn = false;
            HasMoan = HasLibrary = HasBreath = HasSfx = HasBlush = HasGaze = HasGauge = HasAnim = HasBlink = false;
            InSexMode = VoiceOn = SfxOn = BreakSystemOn = DirtyTalkPlaying = DirtyTalkFlag = DialoguePlaying = DirtyTalkPause = FunctionPause = FacialOff = MoanPlaying = MoanTail = OwnVoicePlaying = false;
            _ownOneShot = false;
            FacialLayer = ""; TouchReactionTime = 0f; Desire = -1f;
            LastClip = null; LastSlapClip = null; LastClipLength = 0f;
            _gateT = -100f; _nextFind = 0f; _nextSlow = 0f; _nextSet = 0f; _ownEnd = 0f; _moanFail = 0; _moanOffUntil = 0f;
            _bridgeLogged = false; BridgeLine = "";
            _pitchHold = false; _gHold = false; _trigSet = false;
            _dtDead = false; _guardBackoff = false; _repaired = false; _voiceErr = false; _sfxErr = false; _seMems.Clear();
            _dtStatus = "off"; _dtStKey = -1; _gsVer = -1;
            SetGuardStatus("off");
        }
    }
}
