using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // Her face clips (0.8.7): a survey of every clip in her face Animator's controller (which of them move her face, how much, where),
    // a mood -> clip pick (keywords, then strength; the panel can reassign and the choice is saved), cached face-bone tracks of the picked
    // clips, and a face-only layer that plays one of them over the game's face for a reaction.
    //
    // Survey and track building sample a clip with AnimationClip.SampleAnimation on the Animator's GameObject inside our apply step, a few
    // clips per frame. Everything a sample can touch is snapshot first and restored exactly in the same frame (every transform under the
    // Animator's object: local position, rotation, scale and active flag; every renderer's enabled flag and property block), so no frame
    // ever shows a sampled pose. Which face bones a clip really binds is found with a sentinel pass (bones set to an impossible pose before
    // the sample: what the clip does not write keeps the sentinel), so a clip never freezes a bone it doesn't animate.
    //
    // The layer writes only the bound face bones (local position / rotation), lerped from the game's pose of this frame by the envelope,
    // after the Animator and before our bone-slider trackers (Body calls Step before Apply), so the sliders still add on top and nothing
    // accumulates: the Animator rewrites every bone its controller binds each frame. It never touches the Animator, its layers or weights.
    // The eyeballs (Eye_L/R: the game's look-at) are never written, and the mouth (jaw, lips, tongue, chin) is left to the game while she
    // speaks or moans: the lip sync is baked into the game's clips.
    internal sealed class ReactionsFace
    {
        public const int Pleasure1 = 0, Pleasure2 = 1, Pleasure3 = 2, Ticklish = 3, Surprise = 4, Pain = 5, Climax = 6, NM = 7;
        public static readonly string[] MoodNames = { "Pleasure 1", "Pleasure 2", "Pleasure 3", "Ticklish", "Surprise", "Pain", "Climax" };
        static readonly string[][] MoodWords =
        {
            new[] { "feeling", "moan", "pleasure", "sexfacial", "kimochi", "enjoy", "excite", "aroused" },
            new[] { "feeling", "moan", "pleasure", "sexfacial", "kimochi", "enjoy", "excite", "aroused" },
            new[] { "feeling", "moan", "pleasure", "sexfacial", "kimochi", "enjoy", "excite", "aroused" },
            new[] { "tickle", "laugh", "giggle", "smile", "happy" },   // 0.11.9: no "shy" / "embarrass" (a coy face, not a laugh)
            new[] { "surprise", "shock", "startle", "gasp", "odoro", "scare" },
            new[] { "pain", "hurt", "itai", "frown", "cry", "grimace", "angry", "sad" },
            new[] { "orgasm", "climax", "ahe", "ecsta", "extas", "iku", "cum", "loop_3", "enter_3" },
        };
        const int GEyes = 0, GBrows = 1, GMouth = 2, GCheeks = 3, GOther = 4, NG = 5;
        static readonly string[] GroupNames = { "eyes", "brows", "mouth", "cheeks", "other" };

        sealed class ClipInfo
        {
            public AnimationClip Clip; public string Name = ""; public float Len; public bool Loop, Speech, Blink;
            public float Score; public readonly float[] G = new float[NG]; public string Top = "";
            public bool[] PosB, RotB; public int Bound;
        }
        sealed class Track
        {
            public ClipInfo Ci; public int N, Next; public float Dt; public int[] Bones; public bool[] PosB, RotB;
            public Vector3[] P; public Quaternion[] R; public bool Ready, Failed;
        }

        public string Status = "off";
        public bool Surveyed { get { return _phase == 3; } }
        public int ClipCount { get { return _clips.Count; } }

        Animator _anim; GameObject _go; Transform _root, _head; UnityEngine.Object _ctrl;
        Transform[] _all; Vector3[] _sp, _ss; Quaternion[] _sr; bool[] _sa;
        Renderer[] _rends; MaterialPropertyBlock[] _mpb; bool[] _hasMpb, _ren;
        Transform[] _face; int[] _grp; Vector3[] _bindP; Quaternion[] _bindR; bool[] _hasBind; Vector3[] _snapP; Quaternion[] _snapR;
        int[] _faceIdx;   // face bone -> index into _all
        readonly List<ClipInfo> _clips = new List<ClipInfo>();
        readonly List<ClipInfo> _ranked = new List<ClipInfo>();
        readonly Dictionary<string, Track> _tracks = new Dictionary<string, Track>();
        readonly ClipInfo[] _pick = new ClipInfo[NM];
        readonly string[] _pickWhy = new string[NM];
        // 0.9.0 (from a VaM scene's face reactions: pools of hand-keyed clips per tier, picked at random without the last ones): a few clips
        // per pleasure level and for the climax, all from the picked clip's own name family; the pick is always in its pool
        readonly List<ClipInfo>[] _pool = new List<ClipInfo>[NM];
        readonly int[,] _recent = new int[NM, 2];
        readonly System.Random _rng = new System.Random();
        // 0.9.1: the jaw for audio-driven mouth movement (a voice pack's clips have no lip sync of the game's): the widest jaw opening any
        // face clip writes, as a rotation relative to her neutral jaw (head frame), found by the survey
        int _jaw = -1; Quaternion _jawD = Quaternion.identity; float _jawDeg; string _jawFrom = "";
        // 0.9.7 Surprise (user: "it should be wide-open, shocked eyes and raised eyebrows"; her controller has no clip that widens the eyes
        // or raises the brows, so the strength pick played closing / frowning clips): composed from her face bones, the ones the Face sliders
        // move. Offsets along her up direction in Head_M's frame (pelvis -> chest at bind, so the sign is fixed and it can never close the
        // eyes), mm at full weight; jaw: a fifth of the widest jaw opening. A clip picked in the panel for Surprise still plays instead.
        static readonly string[] SUp = { "Eyelid_LeftUpper_skin_0", "Eyelid_LeftUpper_skin_1", "Eyelid_LeftUpper_skin_2", "Eyelid_RightUpper_skin_0", "Eyelid_RightUpper_skin_1", "Eyelid_RightUpper_skin_2" };
        static readonly string[] SLow = { "Eyelid_LeftLower_skin_0", "Eyelid_LeftLower_skin_1", "Eyelid_LeftLower_skin_2", "Eyelid_RightLower_skin_0", "Eyelid_RightLower_skin_1", "Eyelid_RightLower_skin_2" };
        static readonly string[] SBrow = { "Eyebrow_Left_Middle", "Eyebrow_Left_Outsite", "Eyebrow_Right_Middle", "Eyebrow_Right_Outsite" };
        static readonly string[] SBrowIn = { "Eyebrow_Left_Insite", "Eyebrow_Right_Insite" };
        const float SUpMm = 2.6f, SLowMm = -1.0f, SBrowMm = 4.0f, SBrowInMm = 5.0f, SJaw = 0.22f;
        int[] _sBones; float[] _sMm;
        // 0.11.9 (user: "Tickle expression needs to change too to an agonizing smile/laugh"): the Ticklish face is a helpless laugh. The
        // smile / laugh clip keeps the mouth and cheeks; the Pain clip's squeezed eyes (and half of its knitted brows) go over it; and these
        // offsets on the same lid / brow bones as the built surprise give the "grief" brow: inner brows up, the rest down, lids squeezed.
        const float AUpMm = -1.6f, ALowMm = 1.0f, ABrowMm = -1.0f, ABrowInMm = 3.5f, APainEyes = 0.85f, APainBrows = 0.5f;
        float[] _aMm;
        Track _over;      // the Pain clip's track while Ticklish plays (eyes and brows only)
        Vector3 _upH = Vector3.up;
        bool _sOn, _sRel; float _sT, _sW, _sHold;
        public bool HasBuiltSurprise { get { return _face != null && _sBones != null && _sBones.Length > 0; } }

        // 0.9.9 lip sync: the mouth bones (jaw, lips, cheeks, then tongue / teeth), their neutral local pose (from the bind poses), their
        // parent's frame in the head's frame, and the mouth poses the survey saw (seeds for the ARKit map)
        int[] _lip; Vector3[] _nP; Quaternion[] _nQ; bool[] _hasN; Matrix4x4[] _parH; float[] _mmUnit;
        sealed class LipSample { public string Clip; public Vector3[] P; public Quaternion[] R; public bool[] HasP, HasR; }
        readonly List<LipSample> _lipSamples = new List<LipSample>();
        int _mapVer = -1; int[][] _mapB; Vector3[][] _mapP, _mapR; int[] _mapped; Vector3[] _accP, _accR;
        public bool HasJaw { get { return _phase == 3 && _jaw >= 0 && _jawDeg >= 2f && _face != null && _face[_jaw] != null; } }
        readonly Dictionary<string, string> _hooked = new Dictionary<string, string>();
        string _map = null;
        int _phase, _next;            // 0 not started, 1 surveying, 2 ranking, 3 done
        float _t0, _lastNow;
        bool _sampleBroken, _logged;
        readonly Queue<Track> _build = new Queue<Track>();

        // playback
        Track _cur; int _curMood = -1; float _t, _w, _hold, _env, _att = 0.12f, _relT = 0.45f; bool _rel;
        int _pendMood = -1; float _pendW, _pendHold, _pendUntil;
        // hook watch
        string _watchTag; float _watchUntil; readonly HashSet<string> _watchBase = new HashSet<string>(), _watchNew = new HashSet<string>();

        static readonly Vector3 SentP = new Vector3(123.25f, -77.5f, 9.125f);
        static readonly Quaternion SentR = new Quaternion(0.1825742f, 0.3651484f, 0.5477226f, 0.7302967f);

        static void L(string m) { Plugin.Log.LogInfo("Reactions face: " + m); }

        // ---------------- bones ----------------

        static int GroupOf(string n)
        {
            string s = n.ToLowerInvariant();
            if (s.Contains("brow")) return GBrows;
            if (s.Contains("eyelid") || s.Contains("lash")) return GEyes;
            if (s.Contains("jaw") || s.Contains("lip") || s.Contains("mouth") || s.Contains("tongue") || s.Contains("teeth") || s.Contains("chin")) return GMouth;
            if (s.Contains("cheek") || s.Contains("nose") || s.Contains("nasolabial")) return GCheeks;
            return GOther;
        }

        // the eyeballs follow the game's look-at; hair is DynamicBone's
        static bool Skip(string n)
        {
            string s = n.ToLowerInvariant();
            if (s.Contains("hair")) return true;
            if (s.StartsWith("eye_") || s == "eye_l" || s == "eye_r" || s.StartsWith("eyeball")) return true;
            return false;
        }

        bool Bind(Character her, Animator anim)
        {
            _anim = anim; _go = anim.gameObject; _root = _go.transform; _ctrl = anim.runtimeAnimatorController;
            _all = _root.GetComponentsInChildren<Transform>(true);
            int n = _all.Length;
            _sp = new Vector3[n]; _ss = new Vector3[n]; _sr = new Quaternion[n]; _sa = new bool[n];
            _rends = _root.GetComponentsInChildren<Renderer>(true);
            _mpb = new MaterialPropertyBlock[_rends.Length]; _hasMpb = new bool[_rends.Length]; _ren = new bool[_rends.Length];
            for (int i = 0; i < _mpb.Length; i++) _mpb[i] = new MaterialPropertyBlock();
            _head = null;
            Transform hb;
            if (her != null && her.Bones.TryGetValue("Head_M", out hb) && hb != null) _head = hb;
            var idx = new Dictionary<Transform, int>();
            for (int i = 0; i < n; i++) { idx[_all[i]] = i; if (_head == null && _all[i].name == "Head_M") _head = _all[i]; }
            if (_head == null) { Status = "off: her head bone (Head_M) was not found"; return false; }
            var face = new List<Transform>();
            foreach (var t in _head.GetComponentsInChildren<Transform>(true)) if (t != _head && !Skip(t.name) && idx.ContainsKey(t)) face.Add(t);
            _face = face.ToArray();
            int nf = _face.Length;
            _grp = new int[nf]; _faceIdx = new int[nf]; _bindP = new Vector3[nf]; _bindR = new Quaternion[nf]; _hasBind = new bool[nf];
            _snapP = new Vector3[nf]; _snapR = new Quaternion[nf];
            for (int b = 0; b < nf; b++) { _grp[b] = GroupOf(_face[b].name); _faceIdx[b] = idx[_face[b]]; }
            // the neutral face: each bone's bind pose in the head's frame (from her skinned meshes' bind poses)
            var bp = new Dictionary<Transform, Matrix4x4>();
            if (her != null)
                foreach (var r in her.Renderers)
                {
                    if (r == null || r.sharedMesh == null) continue;
                    var bones = r.bones; var poses = r.sharedMesh.bindposes;
                    if (bones == null || poses == null) continue;
                    for (int i = 0; i < bones.Length && i < poses.Length; i++) if (bones[i] != null && !bp.ContainsKey(bones[i])) bp[bones[i]] = poses[i];
                }
            Matrix4x4 H;
            bool headBind = bp.TryGetValue(_head, out H);
            int nb = 0;
            for (int b = 0; b < nf; b++)
            {
                Matrix4x4 B;
                if (headBind && bp.TryGetValue(_face[b], out B))
                {
                    var M = H * B.inverse;
                    _bindP[b] = M.MultiplyPoint3x4(Vector3.zero); _bindR[b] = M.rotation; _hasBind[b] = true; nb++;
                }
            }
            for (int b = 0; b < nf; b++) { _snapP[b] = _head.InverseTransformPoint(_face[b].position); _snapR[b] = Quaternion.Inverse(_head.rotation) * _face[b].rotation; }
            _jaw = -1; _jawDeg = 0f; _jawD = Quaternion.identity; _jawFrom = "";
            for (int b = 0; b < nf; b++) { string jn = _face[b].name.ToLowerInvariant(); if (jn == "jaw_m") { _jaw = b; break; } if (_jaw < 0 && jn.Contains("jaw")) _jaw = b; }
            BindLip(bp, H, headBind);
            L("bound to '" + _root.name + "' (" + n + " transforms, " + _rends.Length + " renderers), " + nf + " face bones under Head_M (" + nb + " with a bind pose; eyeballs and hair left out)");
            BindSurprise(bp);
            return nf > 0;
        }

        // Only bones whose position the controller writes (some clip has them in PosB): the Animator rewrites those every frame, so our offset
        // never accumulates. Others are dropped (logged).
        void FilterSurprise()
        {
            if (_sBones == null || _sBones.Length == 0) return;
            var keep = new List<int>(); var mm = new List<float>(); var am = new List<float>(); var dropped = new StringBuilder();
            for (int i = 0; i < _sBones.Length; i++)
            {
                int b = _sBones[i]; bool w = false;
                foreach (var c in _clips) if (c.PosB != null && c.PosB[b]) { w = true; break; }
                if (w) { keep.Add(b); mm.Add(_sMm[i]); am.Add(_aMm != null && i < _aMm.Length ? _aMm[i] : 0f); } else { if (dropped.Length > 0) dropped.Append(", "); dropped.Append(_face[b].name); }
            }
            _sBones = keep.ToArray(); _sMm = mm.ToArray(); _aMm = am.ToArray();
            if (dropped.Length > 0) L("surprise: left out (no clip animates their position, an offset would add up): " + dropped);
            if (_sBones.Length == 0) L("surprise: no animated lid or brow bone left: Surprise falls back to a clip");
        }

        void BindSurprise(Dictionary<Transform, Matrix4x4> bp)
        {
            var idx = new List<int>(); var mm = new List<float>(); var am = new List<float>();
            for (int b = 0; b < _face.Length; b++)
            {
                string n = _face[b].name;
                if (Array.IndexOf(SUp, n) >= 0) { idx.Add(b); mm.Add(SUpMm); am.Add(AUpMm); }
                else if (Array.IndexOf(SLow, n) >= 0) { idx.Add(b); mm.Add(SLowMm); am.Add(ALowMm); }
                else if (Array.IndexOf(SBrowIn, n) >= 0) { idx.Add(b); mm.Add(SBrowInMm); am.Add(ABrowInMm); }
                else if (Array.IndexOf(SBrow, n) >= 0) { idx.Add(b); mm.Add(SBrowMm); am.Add(ABrowMm); }
            }
            _sBones = idx.ToArray(); _sMm = mm.ToArray(); _aMm = am.ToArray();
            // her up in Head_M's frame: pelvis -> chest at bind (else as posed now)
            Transform pel = null, chest = null;
            foreach (var t in _all) { if (t == null) continue; if (t.name == "Root_M") pel = t; else if (t.name == "Chest_M") chest = t; }
            Vector3 up = Vector3.zero;
            Matrix4x4 H, Bp, Bc;
            if (pel != null && chest != null && bp.TryGetValue(_head, out H) && bp.TryGetValue(pel, out Bp) && bp.TryGetValue(chest, out Bc))
                up = H.MultiplyVector(Bc.inverse.MultiplyPoint3x4(Vector3.zero) - Bp.inverse.MultiplyPoint3x4(Vector3.zero));
            else if (pel != null && chest != null) up = _head.InverseTransformDirection(chest.position - pel.position);
            _upH = up.sqrMagnitude > 1e-8f ? up.normalized : Vector3.up;
            L("surprise: built from " + _sBones.Length + " face bones (upper lids +" + SUpMm + " mm, lower " + SLowMm + ", brows +" + SBrowMm + ", inner +" + SBrowInMm + ", jaw " + SJaw + " of its widest), her up in the head frame " + _upH.ToString("F2"));
        }

        // ---------------- snapshot / restore ----------------

        void Snapshot()
        {
            for (int i = 0; i < _all.Length; i++)
            {
                var t = _all[i];
                if (t == null) continue;
                t.GetLocalPositionAndRotation(out _sp[i], out _sr[i]); _ss[i] = t.localScale; _sa[i] = t.gameObject.activeSelf;
            }
            for (int i = 0; i < _rends.Length; i++)
            {
                var r = _rends[i];
                if (r == null) continue;
                _ren[i] = r.enabled;
                _hasMpb[i] = r.HasPropertyBlock();
                if (_hasMpb[i]) r.GetPropertyBlock(_mpb[i]);
            }
        }

        void Restore()
        {
            for (int i = 0; i < _all.Length; i++)
            {
                var t = _all[i];
                if (t == null) continue;
                if (t.gameObject.activeSelf != _sa[i]) t.gameObject.SetActive(_sa[i]);
                t.SetLocalPositionAndRotation(_sp[i], _sr[i]);
                if (t.localScale != _ss[i]) t.localScale = _ss[i];
            }
            for (int i = 0; i < _rends.Length; i++)
            {
                var r = _rends[i];
                if (r == null) continue;
                if (r.enabled != _ren[i]) r.enabled = _ren[i];
                if (_hasMpb[i]) r.SetPropertyBlock(_mpb[i]);
                else if (r.HasPropertyBlock()) r.SetPropertyBlock(null);
            }
        }

        void RestoreFace()
        {
            for (int b = 0; b < _face.Length; b++) { int i = _faceIdx[b]; _face[b].SetLocalPositionAndRotation(_sp[i], _sr[i]); }
        }

        // ---------------- per frame (Reactions.Update) ----------------

        // on: Reactions and Face clips are on and the bridge is found. map: the saved mood -> clip names.
        public void Tick(Character her, Animator anim, bool on, string map, float now, float dt)
        {
            _lastNow = now;
            if (!on || anim == null || her == null) { if (_phase != 0 || _cur != null) Reset(on ? "waiting for her face Animator" : "off"); return; }
            if (_anim != anim || _go == null || !ReferenceEquals(anim.runtimeAnimatorController, _ctrl))
            {
                if (_anim != null) L("her face Animator or its controller changed: surveying again");
                Reset("starting");
            }
            if (_sampleBroken) return;
            if (_phase == 0)
            {
                if (!Bind(her, anim)) { _sampleBroken = true; return; }
                var ctrl = anim.runtimeAnimatorController;
                var seen = new HashSet<AnimationClip>();
                if (ctrl != null)
                    foreach (var c in ctrl.animationClips)
                        if (c != null && seen.Add(c))
                        {
                            string ln = c.name.ToLowerInvariant();
                            _clips.Add(new ClipInfo
                            {
                                Clip = c, Name = c.name, Len = c.length, Loop = c.isLooping,
                                Speech = ln.Contains("_dt") || ln.Contains("dt_") || ln.Contains("dirtytalk") || ln.Contains("kiss") || ln.Contains("talk"),
                                Blink = ln.Contains("blink"),
                            });
                        }
                L(_clips.Count + " clips in the controller '" + (ctrl != null ? ctrl.name : "?") + "': surveying 3 per frame");
                _phase = 1; _next = 0; _t0 = now;
            }
            if (_phase == 1)
            {
                int k = 0;
                Snapshot();
                try
                {
                    for (; _next < _clips.Count && k < 3; _next++, k++) SurveyClip(_clips[_next]);
                }
                catch (Exception e) { _sampleBroken = true; Status = "off: sampling failed (" + e.Message + ")"; Plugin.Log.LogWarning("Reactions face: sampling failed, face clips off: " + e); }
                finally { Restore(); }
                if (!_sampleBroken) Status = "surveying her face clips (" + _next + " of " + _clips.Count + ")";
                if (_next >= _clips.Count) _phase = 2;
                return;
            }
            if (_phase == 2) { Rank(now); _phase = 3; ApplyMap(map, true); return; }
            if (map != _map) ApplyMap(map, false);
            // tracks: a few samples per frame
            if (_build.Count > 0)
            {
                Snapshot();
                try
                {
                    int budget = 6;
                    while (budget > 0 && _build.Count > 0)
                    {
                        var tr = _build.Peek();
                        budget -= BuildSome(tr, budget);
                        if (tr.Ready || tr.Failed) _build.Dequeue();
                    }
                }
                catch (Exception e) { _sampleBroken = true; Status = "off: sampling failed (" + e.Message + ")"; Plugin.Log.LogWarning("Reactions face: sampling failed, face clips off: " + e); }
                finally { Restore(); }
            }
            if (_pendMood >= 0)
            {
                if (now > _pendUntil) _pendMood = -1;
                else { int m = _pendMood; string why; if (Play(m, _pendW, _pendHold, out why) || why != "building") _pendMood = -1; }
            }
            WatchTick(now);
        }

        void Reset(string why)
        {
            _phase = 0; _next = 0; _clips.Clear(); _ranked.Clear(); _tracks.Clear(); _build.Clear(); _hooked.Clear(); _lipSamples.Clear(); _lip = null; _mapVer = -1;
            for (int i = 0; i < NM; i++) { _pick[i] = null; _pickWhy[i] = null; if (_pool[i] != null) _pool[i].Clear(); }
            _anim = null; _go = null; _root = null; _head = null; _ctrl = null; _all = null; _rends = null; _face = null;
            _cur = null; _curMood = -1; _pendMood = -1; _sampleBroken = false; _map = null; _watchTag = null;
            _sOn = false; _sBones = null; _sMm = null; _aMm = null; _over = null;
            Status = why;
        }

        public void Release() { Reset("off"); }

        void SurveyClip(ClipInfo ci)
        {
            int nf = _face.Length;
            ci.PosB = new bool[nf]; ci.RotB = new bool[nf];
            float len = Mathf.Max(0f, ci.Len);
            float[] ts = len > 0.01f ? new[] { 0.25f * len, 0.5f * len, 0.75f * len } : new[] { 0f };
            var gs = new float[NG];
            var top = new float[nf];
            // most of the controller's clips are body animations: one sentinel sample tells, and a clip that writes no face bone stops here
            if (!Detect(ci, ts[ts.Length / 2])) { RestoreFace(); return; }
            foreach (var t in ts)
            {
                // pass 1: which face bones the clip writes (a bone it doesn't write keeps the sentinel)
                Detect(ci, t);
                // pass 2: the real pose, measured in the head's frame against her neutral (bind) face
                ci.Clip.SampleAnimation(_go, t);
                Array.Clear(gs, 0, NG);
                Quaternion hInv = Quaternion.Inverse(_head.rotation);
                for (int b = 0; b < nf; b++)
                {
                    if (!ci.PosB[b] && !ci.RotB[b]) continue;
                    var tb = _face[b];
                    Vector3 p = _head.InverseTransformPoint(tb.position);
                    Quaternion q = hInv * tb.rotation;
                    Vector3 p0 = _hasBind[b] ? _bindP[b] : _snapP[b]; Quaternion q0 = _hasBind[b] ? _bindR[b] : _snapR[b];
                    float mm = _head.TransformVector(p - p0).magnitude * 1000f;
                    float deg = ci.RotB[b] ? Quaternion.Angle(q, q0) : 0f;   // an unwritten rotation is the game's current face, not the clip's
                    float s = Mathf.Min(mm, 40f) + 0.2f * Mathf.Min(deg, 90f);
                    if (b == _jaw && ci.RotB[b] && !ci.Speech && deg > _jawDeg && deg < 35f) { _jawDeg = deg; _jawD = Quaternion.Inverse(q0) * q; _jawFrom = ci.Name; }
                    gs[_grp[b]] += s;
                    if (s > top[b]) top[b] = s;
                }
                for (int g = 0; g < NG; g++) if (gs[g] > ci.G[g]) ci.G[g] = gs[g];
                KeepLipSample(ci);
            }
            RestoreFace();
            ci.Score = 0f; ci.Bound = 0;
            for (int g = 0; g < NG; g++) ci.Score += ci.G[g];
            for (int b = 0; b < nf; b++) if (ci.PosB[b] || ci.RotB[b]) ci.Bound++;
            // the three bones it moves most
            var sb = new StringBuilder();
            for (int r = 0; r < 3; r++)
            {
                int bi = -1; float bv = 0.5f;
                for (int b = 0; b < nf; b++) if (top[b] > bv) { bv = top[b]; bi = b; }
                if (bi < 0) break;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(_face[bi].name);
                top[bi] = 0f;
            }
            ci.Top = sb.ToString();
        }

        // Sets every face bone to the sentinel, samples, and marks what the clip wrote (added to ci.PosB / RotB). True = it wrote a face bone.
        bool Detect(ClipInfo ci, float t)
        {
            int nf = _face.Length;
            for (int b = 0; b < nf; b++) _face[b].SetLocalPositionAndRotation(SentP, SentR);
            ci.Clip.SampleAnimation(_go, t);
            bool any = false;
            for (int b = 0; b < nf; b++)
            {
                Vector3 p; Quaternion q; _face[b].GetLocalPositionAndRotation(out p, out q);
                // position only when the clip writes all three axes (a partly written one would keep a sentinel axis in the track)
                if (Mathf.Abs(p.x - SentP.x) > 1e-4f && Mathf.Abs(p.y - SentP.y) > 1e-4f && Mathf.Abs(p.z - SentP.z) > 1e-4f) { ci.PosB[b] = true; any = true; }
                if (Mathf.Abs(Quaternion.Dot(q, SentR)) < 0.99999f) { ci.RotB[b] = true; any = true; }
            }
            RestoreFace();
            return any;
        }

        void Rank(float now)
        {
            _ranked.Clear();
            foreach (var c in _clips) if (c.Bound > 0 && c.Score >= 1f) _ranked.Add(c);
            _ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (_clips.Count > 0 && _ranked.Count == 0)
            {
                bool anyBound = false; foreach (var c in _clips) if (c.Bound > 0) { anyBound = true; break; }
                if (!anyBound) { _sampleBroken = true; Status = "off: sampling moved none of her face bones (SampleAnimation does nothing on this Animator?)"; L(Status); return; }
            }
            // log: the ranked list, 5 per line, once per survey
            var sb = new StringBuilder();
            sb.Append(_ranked.Count).Append(" of ").Append(_clips.Count).Append(" clips move her face (surveyed in ").Append((now - _t0).ToString("0.0")).Append(" s)");
            L(sb.ToString());
            FilterSurprise();
            L(_jaw < 0 ? "jaw: no jaw bone among her face bones (no audio-driven mouth)" : _jawDeg < 2f ? "jaw: no clip opens her jaw (no audio-driven mouth)"
              : "jaw: " + _face[_jaw].name + " opens up to " + _jawDeg.ToString("0") + " deg (widest in '" + _jawFrom + "'): used for the voice pack's mouth");
            for (int i = 0; i < _ranked.Count && i < 40; i += 5)
            {
                sb.Length = 0;
                for (int k = i; k < i + 5 && k < _ranked.Count && k < 40; k++)
                {
                    if (k > i) sb.Append(" | ");
                    sb.Append(k + 1).Append(". ").Append(Describe(_ranked[k]));
                }
                L(sb.ToString());
            }
            WriteFile();
            Status = "surveyed: " + _ranked.Count + " of " + _clips.Count + " clips move her face";
        }

        string Describe(ClipInfo c)
        {
            var sb = new StringBuilder();
            sb.Append(c.Name).Append(' ').Append(c.Len.ToString("0.00")).Append("s ").Append(c.Score.ToString("0"));
            sb.Append(" (eyes ").Append(c.G[GEyes].ToString("0")).Append(", brows ").Append(c.G[GBrows].ToString("0")).Append(", mouth ").Append(c.G[GMouth].ToString("0"))
              .Append(", cheeks ").Append(c.G[GCheeks].ToString("0")).Append(')');
            if (c.Speech) sb.Append(" [speech]");
            if (c.Blink) sb.Append(" [blink]");
            if (c.Loop) sb.Append(" [loop]");
            return sb.ToString();
        }

        void WriteFile()
        {
            try
            {
                Directory.CreateDirectory(Store.Dir);
                string path = Path.Combine(Store.Dir, "face-clips.txt");
                var sb = new StringBuilder();
                sb.Append("# SF Body Morphs ").Append(Plugin.Version).Append(": her face Animator's clips ranked by how much they move her face\n");
                sb.Append("# score = sum over the face bones it writes of mm (capped 40) + 0.2 x degrees (capped 90), against her bind (neutral) face, max of 3 samples\n");
                sb.Append("# rank\tname\tlength\tloop\tscore\teyes\tbrows\tmouth\tcheeks\tother\tbones\tmoves most\ttags\n");
                int r = 0;
                foreach (var c in _ranked)
                {
                    r++;
                    sb.Append(r).Append('\t').Append(c.Name).Append('\t').Append(c.Len.ToString("0.00")).Append('\t').Append(c.Loop ? "y" : "n").Append('\t').Append(c.Score.ToString("0.0"));
                    for (int g = 0; g < NG; g++) sb.Append('\t').Append(c.G[g].ToString("0.0"));
                    sb.Append('\t').Append(c.Bound).Append('\t').Append(c.Top).Append('\t').Append(c.Speech ? "speech " : "").Append(c.Blink ? "blink" : "").Append('\n');
                }
                sb.Append("# clips that move none of her face bones: ").Append(_clips.Count - _ranked.Count).Append('\n');
                foreach (var c in _clips) if (!_ranked.Contains(c)) sb.Append("-\t").Append(c.Name).Append('\t').Append(c.Len.ToString("0.00")).Append('\n');
                File.WriteAllText(path, sb.ToString());
                L("ranked list saved to " + path);
            }
            catch (Exception e) { L("could not save the ranked list: " + e.Message); }
        }

        // ---------------- moods ----------------

        // map: "Pleasure 1=clip;Pain=clip;..." (the panel's choices). Unnamed moods are picked by keyword, then by strength.
        void ApplyMap(string map, bool log)
        {
            _map = map;
            var chosen = new Dictionary<int, string>();
            if (!string.IsNullOrEmpty(map))
                foreach (var part in map.Split(';'))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    int m = Array.IndexOf(MoodNames, part.Substring(0, eq).Trim());
                    if (m >= 0) chosen[m] = part.Substring(eq + 1).Trim();
                }
            var used = new HashSet<ClipInfo>();
            for (int m = 0; m < NM; m++)
            {
                _pick[m] = null; _pickWhy[m] = null;
                string nm;
                if (chosen.TryGetValue(m, out nm)) { var c = ByName(nm); if (c != null) { _pick[m] = c; _pickWhy[m] = "your pick"; used.Add(c); } }
            }
            // keywords
            for (int m = 0; m < NM; m++)
            {
                if (_pick[m] != null) continue;
                var cands = new List<ClipInfo>();
                foreach (var c in _ranked) if (Usable(c) && HasWord(c.Name, MoodWords[m])) cands.Add(c);
                if (cands.Count == 0) continue;
                if (m <= Pleasure3)
                {
                    // weakest to strongest: Pleasure 1 / 2 / 3
                    cands.Sort((a, b) => a.Score.CompareTo(b.Score));
                    int i = m == Pleasure1 ? 0 : m == Pleasure2 ? cands.Count / 2 : cands.Count - 1;
                    _pick[m] = cands[i];
                }
                else
                {
                    ClipInfo best = null;
                    foreach (var c in cands) if (!used.Contains(c) && (best == null || c.Score > best.Score)) best = c;
                    _pick[m] = best ?? cands[0];
                }
                _pickWhy[m] = "by name"; used.Add(_pick[m]);
            }
            BuildPools();
            // by strength: what is still empty
            var free = new List<ClipInfo>();
            // 0.8.9 (log: Surprise picked 'dance_P' by strength and played it 21 times): strength-only picks skip clips named for body
            // motions (dance, walk, idle, pose, sex positions); those move her face bones as a side effect, not as an expression
            foreach (var c in _ranked) if (Usable(c) && !used.Contains(c) && !NotAnExpression(c.Name)) free.Add(c);
            for (int m = 0; m < NM; m++)
            {
                if (_pick[m] != null) continue;
                ClipInfo c = null;
                if (m <= Pleasure3)
                {
                    // the strongest faces with eyes and mouth, weakest of them for level 1
                    var pl = new List<ClipInfo>();
                    foreach (var x in free) if (x.G[GEyes] + x.G[GBrows] > 0f && x.G[GMouth] > 0f) pl.Add(x);
                    if (pl.Count == 0) pl.AddRange(free);
                    if (pl.Count > 0)
                    {
                        int n = Mathf.Min(pl.Count, 6);
                        pl.Sort((a, b) => b.Score.CompareTo(a.Score));
                        var top = pl.GetRange(0, n); top.Sort((a, b) => a.Score.CompareTo(b.Score));
                        c = top[m == Pleasure1 ? 0 : m == Pleasure2 ? n / 2 : n - 1];
                    }
                }
                else if (m == Climax) c = _pick[Pleasure3];
                else if (m == Ticklish) c = _pick[Pleasure1];
                else
                {
                    // pain: the most brows; surprise: the most eyes (both among what is left)
                    int g = m == Pain ? GBrows : GEyes;
                    foreach (var x in free) if (!used.Contains(x) && (c == null || x.G[g] > c.G[g])) c = x;
                }
                if (c != null) { _pick[m] = c; _pickWhy[m] = m == Climax || m == Ticklish ? "same as " + MoodNames[m == Climax ? Pleasure3 : Pleasure1] : "by strength only"; used.Add(c); }
            }
            if (HasBuiltSurprise && _pickWhy[Surprise] != "your pick") { _pick[Surprise] = null; _pickWhy[Surprise] = "built: wide eyes, raised brows"; }
            if (_pick[Ticklish] != null) _pickWhy[Ticklish] += "; laughing in agony: Pain's eyes / brows and grief brows over it";
            if (log || !_logged)
            {
                _logged = true;
                var sb = new StringBuilder("moods: ");
                for (int m = 0; m < NM; m++)
                {
                    if (m > 0) sb.Append(", ");
                    sb.Append(MoodNames[m]).Append(" = ").Append(_pick[m] != null ? "'" + _pick[m].Name + "' (" + _pickWhy[m] + ")" : _pickWhy[m] != null ? _pickWhy[m] : "none");
                }
                L(sb.ToString());
            }
            // the picked clips' tracks are built ahead (a few samples per frame)
            for (int m = 0; m < NM; m++)
            {
                if (_pick[m] != null) TrackOf(_pick[m]);
                if (_pool[m] != null) foreach (var c in _pool[m]) TrackOf(c);
            }
            if (log || !_poolLogged)
            {
                var sb = new StringBuilder();
                for (int m = 0; m < NM; m++)
                {
                    if (_pool[m] == null || _pool[m].Count < 2) continue;
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(MoodNames[m]).Append(": ");
                    for (int i = 0; i < _pool[m].Count; i++) { if (i > 0) sb.Append(", "); sb.Append(_pool[m][i].Name); }
                }
                if (sb.Length > 0) { _poolLogged = true; L("mood pools: " + sb); }
            }
        }
        bool _poolLogged;

        // The pools: for each pleasure level picked by name, up to 4 clips of the same keyword family around its strength (weakest third /
        // middle third / strongest third); the climax: its pick, the strongest climax-named clips and the strongest third of the pleasure family.
        // A mood picked in the panel (or by strength) keeps exactly that clip.
        void BuildPools()
        {
            for (int m = 0; m < NM; m++) { if (_pool[m] == null) _pool[m] = new List<ClipInfo>(); _pool[m].Clear(); _recent[m, 0] = _recent[m, 1] = -1; }
            var fam = new List<ClipInfo>();
            foreach (var c in _ranked) if (Usable(c) && HasWord(c.Name, MoodWords[Pleasure1]) && !NotAnExpression(c.Name)) fam.Add(c);
            fam.Sort((a, b) => a.Score.CompareTo(b.Score));
            int n = fam.Count;
            for (int m = Pleasure1; m <= Pleasure3; m++)
            {
                if (_pick[m] == null) continue;
                _pool[m].Add(_pick[m]);
                if (_pickWhy[m] != "by name" || n < 3) continue;
                int lo = (m - Pleasure1) * n / 3, hi = (m - Pleasure1 + 1) * n / 3;
                for (int i = lo; i < hi && _pool[m].Count < 4; i++) if (!_pool[m].Contains(fam[i])) _pool[m].Add(fam[i]);
            }
            if (_pick[Climax] != null)
            {
                _pool[Climax].Add(_pick[Climax]);
                if (_pickWhy[Climax] == "by name")
                {
                    var cl = new List<ClipInfo>();
                    foreach (var c in _ranked) if (Usable(c) && HasWord(c.Name, MoodWords[Climax]) && !NotAnExpression(c.Name)) cl.Add(c);
                    cl.Sort((a, b) => b.Score.CompareTo(a.Score));
                    for (int i = 0; i < cl.Count && _pool[Climax].Count < 3; i++) if (!_pool[Climax].Contains(cl[i])) _pool[Climax].Add(cl[i]);
                    for (int i = n - 1; i >= 2 * n / 3 && i >= 0 && _pool[Climax].Count < 5; i--) if (!_pool[Climax].Contains(fam[i])) _pool[Climax].Add(fam[i]);
                }
            }
        }

        // One clip of the mood's pool: at random among the ready ones, never one of the last two played (when the pool has more).
        ClipInfo Choose(int m)
        {
            var pl = _pool[m];
            if (pl == null || pl.Count <= 1) return _pick[m];
            int pick = -1, seen = 0;
            for (int i = 0; i < pl.Count; i++)
            {
                if (i == _recent[m, 0] || (pl.Count > 2 && i == _recent[m, 1])) continue;
                Track tr;
                if (!_tracks.TryGetValue(pl[i].Name, out tr) || !tr.Ready || tr.Failed) continue;
                seen++;
                if (_rng.Next(seen) == 0) pick = i;   // reservoir: uniform over the candidates
            }
            if (pick < 0) return _pick[m];
            _recent[m, 1] = _recent[m, 0]; _recent[m, 0] = pick;
            return pl[pick];
        }

        static bool Usable(ClipInfo c) { return !c.Speech && !c.Blink && c.Len > 0.05f; }

        static readonly string[] BodyMotionWords = { "dance", "walk", "idle", "pose", "sit", "stand", "cowgirl", "missionary", "doggy", "blowjob", "handjob", "sex_" };
        static bool NotAnExpression(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            foreach (var w in BodyMotionWords) if (n.Contains(w)) return true;
            return false;
        }
        static bool HasWord(string n, string[] words) { string s = n.ToLowerInvariant(); foreach (var w in words) if (s.Contains(w)) return true; return false; }
        ClipInfo ByName(string n) { foreach (var c in _ranked) if (c.Name == n) return c; foreach (var c in _clips) if (c.Name == n && c.Bound > 0) return c; return null; }

        public string PickName(int m) { return m >= 0 && m < NM && _pick[m] != null ? _pick[m].Name : ""; }
        public string PickWhy(int m) { return m >= 0 && m < NM && _pickWhy[m] != null ? _pickWhy[m] : ""; }
        public int RankedCount { get { return _ranked.Count; } }

        // The panel's < / >: the next clip of the ranked list for a mood; returns the new map string (to save in the config).
        public string Cycle(int m, int dir)
        {
            if (m < 0 || m >= NM || _ranked.Count == 0) return _map ?? "";
            int i = _pick[m] != null ? _ranked.IndexOf(_pick[m]) : -1;
            i = ((i + dir) % _ranked.Count + _ranked.Count) % _ranked.Count;
            var sb = new StringBuilder();
            for (int k = 0; k < NM; k++)
            {
                string nm = k == m ? _ranked[i].Name : (_pickWhy[k] == "your pick" && _pick[k] != null ? _pick[k].Name : null);
                if (nm == null) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(MoodNames[k]).Append('=').Append(nm);
            }
            return sb.ToString();
        }

        // ---------------- tracks ----------------

        Track TrackOf(ClipInfo ci)
        {
            Track tr;
            if (_tracks.TryGetValue(ci.Name, out tr)) return tr;
            var bones = new List<int>();
            for (int b = 0; b < _face.Length; b++) if (ci.PosB[b] || ci.RotB[b]) bones.Add(b);
            float len = Mathf.Clamp(ci.Len, 0.05f, 6f);
            tr = new Track { Ci = ci, Dt = 1f / 15f, Bones = bones.ToArray() };
            tr.N = Mathf.Max(2, Mathf.CeilToInt(len / tr.Dt) + 1);
            tr.PosB = new bool[tr.Bones.Length]; tr.RotB = new bool[tr.Bones.Length];
            for (int k = 0; k < tr.Bones.Length; k++) { tr.PosB[k] = ci.PosB[tr.Bones[k]]; tr.RotB[k] = ci.RotB[tr.Bones[k]]; }
            tr.P = new Vector3[tr.N * tr.Bones.Length]; tr.R = new Quaternion[tr.N * tr.Bones.Length];
            _tracks[ci.Name] = tr;
            if (tr.Bones.Length == 0) tr.Failed = true; else _build.Enqueue(tr);
            return tr;
        }

        int BuildSome(Track tr, int budget)
        {
            int done = 0;
            int nb = tr.Bones.Length;
            while (done < budget && tr.Next < tr.N)
            {
                float t = Mathf.Min(tr.Next * tr.Dt, tr.Ci.Len);
                tr.Ci.Clip.SampleAnimation(_go, t);
                for (int k = 0; k < nb; k++) _face[tr.Bones[k]].GetLocalPositionAndRotation(out tr.P[tr.Next * nb + k], out tr.R[tr.Next * nb + k]);
                tr.Next++; done++;
            }
            if (tr.Next >= tr.N) tr.Ready = true;
            return Mathf.Max(1, done);
        }

        // ---------------- playback ----------------

        static int Prio(int m) { return m == Climax ? 3 : m == Pain ? 2 : m == Surprise ? 1 : 0; }

        // Starts a mood's face: weight 0..1 at the top of the envelope, held `hold` s (plus the attack and the release). False = not played
        // (why: "no clip", "building" (it plays when ready, within 3 s), "busy" (a stronger face is on), "hook" (the game's own hook plays it)).
        public bool Play(int m, float weight, float hold, out string why)
        {
            why = null;
            if (_phase != 3 || _sampleBroken) { why = _sampleBroken ? "off" : "surveying"; return false; }
            if (m == Surprise && _pick[m] == null && HasBuiltSurprise)
            {
                if (_cur != null && !_rel && Prio(_curMood) > Prio(m)) { why = "busy"; return false; }
                _sOn = true; _sRel = false; _sT = 0f; _sW = Mathf.Clamp01(weight); _sHold = Mathf.Max(0.1f, hold);
                return true;
            }
            if (m < 0 || m >= NM || _pick[m] == null) { why = "no clip"; return false; }
            if (_cur != null && !_rel && Prio(_curMood) > Prio(m)) { why = "busy"; return false; }
            var ci = Choose(m);
            // 0.9.0 (0.8.8 log: both climaxes were "left to the game's own hook"): a clip the game's eye reaction once played is left to it only
            // while the game is playing it now, and never for the climax (ours is not the game's hook firing). 0.8.7 kept it hooked for good.
            string hk;
            if (m != Climax && _hooked.TryGetValue(ci.Name, out hk) && PlayingNow(ci.Name)) { why = "hook"; return false; }
            var tr = TrackOf(ci);
            if (tr.Failed) { why = "no clip"; return false; }
            if (!tr.Ready) { _pendMood = m; _pendW = weight; _pendHold = hold; _pendUntil = _lastNow + 3f; why = "building"; return false; }
            _cur = tr; _curMood = m; _t = 0f; _w = Mathf.Clamp01(weight); _hold = Mathf.Max(0.1f, hold); _rel = false;
            _over = null;
            if (m == Ticklish && _pick[Pain] != null && _pick[Pain] != ci)
            {
                var ot = TrackOf(_pick[Pain]);
                if (ot.Ready && !ot.Failed) _over = ot;
            }
            // pleasure and climax fade in and out slowly (the scene's 0.75 s crossfade); a wince or a start is quick (its peak at 0.2-0.4 s)
            bool slow = m <= Pleasure3 || m == Climax;
            _att = slow ? 0.35f : 0.12f; _relT = slow ? 0.75f : 0.45f;
            return true;
        }

        public string CurrentName { get { return _cur != null ? _cur.Ci.Name + (_curMood == Ticklish ? (_over != null ? " + '" + _over.Ci.Name + "' eyes/brows" : "") + " + grief brows" : "") : _sOn ? "built surprise" : ""; } }
        public int CurrentMood { get { return _cur != null ? _curMood : -1; } }

        // Body.ApplyAll for her, after the Animator and before the bone sliders: the face of this frame. mouthFree: she isn't speaking or moaning.
        public void Step(float dt, bool mouthFree, float scale)
        {
            ClipStep(dt, mouthFree, scale);
            SurpriseStep(dt, mouthFree, scale);
        }

        // the built surprise: fast in (60 ms), held, out over 0.4 s
        void SurpriseStep(float dt, bool mouthFree, float scale)
        {
            if (!_sOn || _face == null || _sBones == null || _anim == null || !_anim.isActiveAndEnabled || _head == null) return;
            _sT += dt;
            const float att = 0.06f, rel = 0.4f;
            float env = _sT < att ? _sT / att : _sT < att + _sHold ? 1f : 1f - (_sT - att - _sHold) / rel;
            if (_sT >= att + _sHold) _sRel = true;
            if (env <= 0f) { _sOn = false; return; }
            float w = Mathf.Clamp01(env * _sW * scale);
            for (int i = 0; i < _sBones.Length; i++)
            {
                var t = _face[_sBones[i]];
                if (t == null || t.parent == null) continue;
                Vector3 world = _head.TransformVector(_upH * (_sMm[i] * 0.001f * w));
                t.localPosition += t.parent.InverseTransformVector(world);
            }
            if (mouthFree) JawStep(SJaw * w);
        }

        void ClipStep(float dt, bool mouthFree, float scale)
        {
            if (_cur == null || _face == null || _anim == null || !_anim.isActiveAndEnabled) return;
            _t += dt;
            float attack = _att, release = _relT;
            float env;
            if (_t < attack) env = _t / attack;
            else if (_t < attack + _hold) env = 1f;
            else { _rel = true; env = 1f - (_t - attack - _hold) / release; }
            if (env <= 0f) { _cur = null; _curMood = -1; _rel = false; return; }
            float w = Mathf.Clamp01(env * _w * scale);
            if (w <= 0f) return;
            var tr = _cur;
            float len = tr.Ci.Len;
            float ct = tr.Ci.Loop && len > 0.05f ? _t % len : Mathf.Min(_t, len);
            float fi = Mathf.Clamp(ct / tr.Dt, 0f, tr.N - 1);
            int i0 = Mathf.Min((int)fi, tr.N - 1), i1 = Mathf.Min(i0 + 1, tr.N - 1);
            float f = fi - i0;
            int nb = tr.Bones.Length;
            for (int k = 0; k < nb; k++)
            {
                int b = tr.Bones[k];
                var t = _face[b];
                if (t == null) continue;
                // 0.11.11: while she speaks the mood's mouth / cheek pose is kept for LipStep (the visemes go on top of it) instead of dropped
                if (!mouthFree && (_grp[b] == GMouth || _grp[b] == GCheeks)) KeepMood(b, tr, k, nb, i0, i1, f, w);
                if (!mouthFree && _grp[b] == GMouth) continue;
                Vector3 p; Quaternion q; t.GetLocalPositionAndRotation(out p, out q);
                if (tr.PosB[k]) p = Vector3.LerpUnclamped(p, Vector3.LerpUnclamped(tr.P[i0 * nb + k], tr.P[i1 * nb + k], f), w);
                if (tr.RotB[k]) q = Quaternion.SlerpUnclamped(q, Quaternion.SlerpUnclamped(tr.R[i0 * nb + k], tr.R[i1 * nb + k], f), w);
                t.SetLocalPositionAndRotation(p, q);
            }
            if (_curMood == Ticklish) AgonyStep(w, mouthFree);
        }

        // 0.11.11: the mood's pose of a mouth / cheek bone this frame (clip pose at its time, weight w) while her voice holds the mouth;
        // LipStep blends it in under the lip-sync deltas so a smile or a grimace stays while she speaks. Frame-stamped: stale = not used.
        Vector3[] _moodP; Quaternion[] _moodQ; float[] _moodW; bool[] _moodHasP, _moodHasR; int[] _moodFrame;
        void KeepMood(int b, Track tr, int k, int nb, int i0, int i1, float f, float w)
        {
            int nf = _face.Length;
            if (_moodP == null || _moodP.Length != nf)
            { _moodP = new Vector3[nf]; _moodQ = new Quaternion[nf]; _moodW = new float[nf]; _moodHasP = new bool[nf]; _moodHasR = new bool[nf]; _moodFrame = new int[nf]; }
            _moodHasP[b] = tr.PosB[k]; _moodHasR[b] = tr.RotB[k];
            if (tr.PosB[k]) _moodP[b] = Vector3.LerpUnclamped(tr.P[i0 * nb + k], tr.P[i1 * nb + k], f);
            if (tr.RotB[k]) _moodQ[b] = Quaternion.SlerpUnclamped(tr.R[i0 * nb + k], tr.R[i1 * nb + k], f);
            _moodW[b] = w; _moodFrame[b] = Time.frameCount;
        }

        // 0.11.9: the helpless laugh over the Ticklish clip: the Pain clip's eyes (and half its brows) at its own time, then the grief-brow /
        // squeezed-lid offsets. The offsets go on bones the Animator rewrites every frame (FilterSurprise), so nothing adds up.
        void AgonyStep(float w, bool mouthFree)
        {
            var ot = _over;
            if (ot != null && ot.Ready && !ot.Failed)
            {
                float len = ot.Ci.Len;
                float ct = ot.Ci.Loop && len > 0.05f ? _t % len : Mathf.Min(_t, len);
                float fi = Mathf.Clamp(ct / ot.Dt, 0f, ot.N - 1);
                int i0 = Mathf.Min((int)fi, ot.N - 1), i1 = Mathf.Min(i0 + 1, ot.N - 1);
                float f = fi - i0;
                int nb = ot.Bones.Length;
                for (int k = 0; k < nb; k++)
                {
                    int b = ot.Bones[k];
                    float g = _grp[b] == GEyes ? APainEyes : _grp[b] == GBrows ? APainBrows : 0f;
                    if (g <= 0f) continue;
                    var t = _face[b];
                    if (t == null) continue;
                    float wb = w * g;
                    Vector3 p; Quaternion q; t.GetLocalPositionAndRotation(out p, out q);
                    if (ot.PosB[k]) p = Vector3.LerpUnclamped(p, Vector3.LerpUnclamped(ot.P[i0 * nb + k], ot.P[i1 * nb + k], f), wb);
                    if (ot.RotB[k]) q = Quaternion.SlerpUnclamped(q, Quaternion.SlerpUnclamped(ot.R[i0 * nb + k], ot.R[i1 * nb + k], f), wb);
                    t.SetLocalPositionAndRotation(p, q);
                }
            }
            if (_aMm == null || _sBones == null || _head == null) return;
            for (int i = 0; i < _sBones.Length && i < _aMm.Length; i++)
            {
                var t = _face[_sBones[i]];
                if (t == null || t.parent == null || _aMm[i] == 0f) continue;
                Vector3 world = _head.TransformVector(_upH * (_aMm[i] * 0.001f * w));
                t.localPosition += t.parent.InverseTransformVector(world);
            }
        }

        public void Stop() { _cur = null; _curMood = -1; _pendMood = -1; _sOn = false; }

        // 0.9.1: open 0..1 of the widest jaw opening, on top of what the Animator (and a face clip) left on the jaw this frame.
        public void JawStep(float open)
        {
            if (!HasJaw || !(open > 0.001f) || _anim == null || !_anim.isActiveAndEnabled) return;
            var t = _face[_jaw];
            t.localRotation = t.localRotation * Quaternion.SlerpUnclamped(Quaternion.identity, _jawD, Mathf.Clamp01(open));
        }

        // ---------------- 0.9.9: lip sync (LipSync.cs) ----------------

        public bool LipReady { get { return _phase == 3 && !_sampleBroken && _lip != null && _lip.Length > 0 && _anim != null; } }
        public int LipCount { get { return _lip != null ? _lip.Length : 0; } }
        public string LipName(int i) { return _lip != null && i >= 0 && i < _lip.Length && _face[_lip[i]] != null ? _face[_lip[i]].name : ""; }
        public bool LipIsTongue(int i) { string n = LipName(i).ToLowerInvariant(); return n.Contains("tongue") || n.Contains("teeth") || n.Contains("tooth"); }
        // mm per local position unit of the bone (its parent's scale), for the editor's sliders
        public float LipMm(int i) { return _mmUnit != null && i >= 0 && i < _mmUnit.Length ? _mmUnit[i] : 1000f; }
        public int LipSampleCount { get { return _lipSamples.Count; } }

        static int Order(string n)
        {
            string s = n.ToLowerInvariant();
            if (s.Contains("jaw")) return 0;
            if (s.StartsWith("lip_") || s.Contains("lip")) return 1;
            if (s.Contains("cheek")) return 2;
            if (s.Contains("nose") || s.Contains("nasolabial") || s.Contains("chin") || s.Contains("mouth")) return 3;
            return 4;   // tongue, teeth
        }

        // The mouth bones and their neutral local pose: the bone's bind pose in the head's frame seen from its parent (the parent's bind pose
        // if it is a face bone with one, else its current frame: 'Facial' and similar holders are not animated).
        void BindLip(Dictionary<Transform, Matrix4x4> bp, Matrix4x4 H, bool headBind)
        {
            int nf = _face.Length;
            var lip = new List<int>();
            for (int b = 0; b < nf; b++) if (_grp[b] == GMouth || _grp[b] == GCheeks) lip.Add(b);
            lip.Sort((a, c) => { int d = Order(_face[a].name).CompareTo(Order(_face[c].name)); return d != 0 ? d : string.CompareOrdinal(_face[a].name, _face[c].name); });
            _lip = lip.ToArray();
            int nl = _lip.Length;
            _nP = new Vector3[nl]; _nQ = new Quaternion[nl]; _hasN = new bool[nl]; _parH = new Matrix4x4[nl]; _mmUnit = new float[nl];
            var faceOf = new Dictionary<Transform, int>();
            for (int b = 0; b < nf; b++) faceOf[_face[b]] = b;
            Matrix4x4 headW2L = _head.worldToLocalMatrix;
            int neutral = 0;
            for (int i = 0; i < nl; i++)
            {
                var t = _face[_lip[i]];
                var par = t.parent;
                int pi;
                Matrix4x4 PH;
                Matrix4x4 PB;
                if (par == _head) PH = Matrix4x4.identity;
                else if (par != null && faceOf.TryGetValue(par, out pi) && headBind && bp.TryGetValue(par, out PB)) PH = H * PB.inverse;
                else PH = par != null ? headW2L * par.localToWorldMatrix : Matrix4x4.identity;
                _parH[i] = PH;
                _mmUnit[i] = par != null ? par.TransformVector(Vector3.right).magnitude * 1000f : 1000f;
                Matrix4x4 B;
                if (headBind && bp.TryGetValue(t, out B))
                {
                    var loc = PH.inverse * (H * B.inverse);
                    _nP[i] = loc.GetColumn(3); _nQ[i] = loc.rotation; _hasN[i] = true; neutral++;
                }
                else { t.GetLocalPositionAndRotation(out _nP[i], out _nQ[i]); }
            }
            _lipSamples.Clear(); _mapVer = -1;
            L("lip sync: " + nl + " mouth bones (" + neutral + " with a neutral pose from the bind poses)");
        }

        // the survey's sample of this clip at one time: the mouth bones it writes, as local poses
        void KeepLipSample(ClipInfo ci)
        {
            if (_lip == null || _lipSamples.Count >= 4000) return;
            LipSample s = null;
            for (int i = 0; i < _lip.Length; i++)
            {
                int b = _lip[i];
                if (!ci.PosB[b] && !ci.RotB[b]) continue;
                if (s == null) s = new LipSample { Clip = ci.Name, P = new Vector3[_lip.Length], R = new Quaternion[_lip.Length], HasP = new bool[_lip.Length], HasR = new bool[_lip.Length] };
                Vector3 p; Quaternion q; _face[b].GetLocalPositionAndRotation(out p, out q);
                s.P[i] = p; s.R[i] = q; s.HasP[i] = ci.PosB[b]; s.HasR[i] = ci.RotB[b];
            }
            if (s != null) _lipSamples.Add(s);
        }

        void ResolveMap()
        {
            if (_mapVer == LipSync.MapVersion && _mapB != null) return;
            _mapVer = LipSync.MapVersion;
            int K = LipSync.K, nl = _lip.Length;
            _mapB = new int[K][]; _mapP = new Vector3[K][]; _mapR = new Vector3[K][];
            var mapped = new bool[nl];
            for (int s = 0; s < K; s++)
            {
                var l = LipSync.Map[s];
                var bi = new List<int>(); var ps = new List<Vector3>(); var rs = new List<Vector3>();
                foreach (var e in l)
                {
                    int i = -1;
                    for (int k = 0; k < nl; k++) if (_face[_lip[k]].name == e.Bone) { i = k; break; }
                    if (i < 0) continue;
                    bi.Add(i); ps.Add(e.P); rs.Add(e.R); mapped[i] = true;
                }
                _mapB[s] = bi.ToArray(); _mapP[s] = ps.ToArray(); _mapR[s] = rs.ToArray();
            }
            var ml = new List<int>();
            for (int i = 0; i < nl; i++) if (mapped[i]) ml.Add(i);
            _mapped = ml.ToArray();
            _accP = new Vector3[nl]; _accR = new Vector3[nl];
        }

        // Body.ApplyAll for her, after Step: the mouth from the ARKit weights w (LipSync.K) through the map. fade 0..1 blends from the
        // Animator's mouth of this frame; over 0..1 is how much of the game's mouth is replaced by her neutral mouth under the deltas
        // (0 = the deltas add on the game's mouth). mood 0..1: how much of her face clip's mouth (KeepMood, this frame) goes under the deltas
        // in place of that base. Only bones the map poses are written. False = nothing written (no posed bone).
        public bool LipStep(float[] w, float fade, float over, float mood)
        {
            if (!LipReady || !(fade > 0.001f) || !_anim.isActiveAndEnabled) return false;
            ResolveMap();
            if (_mapped.Length == 0) return false;
            foreach (int i in _mapped) { _accP[i] = Vector3.zero; _accR[i] = Vector3.zero; }
            for (int s = 0; s < LipSync.K; s++)
            {
                float ws = w[s];
                if (ws < 1e-4f) continue;
                var bi = _mapB[s]; var ps = _mapP[s]; var rs = _mapR[s];
                for (int k = 0; k < bi.Length; k++) { _accP[bi[k]] += ps[k] * ws; _accR[bi[k]] += rs[k] * ws; }
            }
            fade = Mathf.Clamp01(fade); over = Mathf.Clamp01(over); mood = Mathf.Clamp01(mood);
            int frame = Time.frameCount;
            foreach (int i in _mapped)
            {
                int b = _lip[i];
                var t = _face[b];
                if (t == null) continue;
                Vector3 p; Quaternion q; t.GetLocalPositionAndRotation(out p, out q);
                float o = _hasN[i] ? over : 0f;
                Vector3 bp = Vector3.LerpUnclamped(p, _nP[i], o);
                Quaternion bq = Quaternion.Slerp(q, _nQ[i], o);
                // the expression's mouth under the visemes (the mood clip's pose, not the game's moan mouth that `over` takes out)
                if (mood > 0f && _moodFrame != null && b < _moodFrame.Length && _moodFrame[b] == frame)
                {
                    float m = mood * _moodW[b];
                    if (_moodHasP[b]) bp = Vector3.LerpUnclamped(bp, _moodP[b], m);
                    if (_moodHasR[b]) bq = Quaternion.Slerp(bq, _moodQ[b], m);
                }
                Vector3 tp = bp + _accP[i];
                Quaternion tq = bq * LipSync.Exp(_accR[i]);
                t.SetLocalPositionAndRotation(Vector3.LerpUnclamped(p, tp, fade), Quaternion.Slerp(q, tq, fade));
            }
            return true;
        }

        // 0.11.7 lip sync probe: the mouth bones the last LipStep moved most (mm / deg added by the map's poses), for the log
        public string LipDeltaReport()
        {
            if (!LipReady || _mapped == null || _mapped.Length == 0) return "no mapped bones";
            var sb = new System.Text.StringBuilder();
            int shown = 0;
            var used = new bool[_lip.Length];
            for (int pick = 0; pick < 4; pick++)
            {
                int best = -1; float bm = 0f;
                foreach (int i in _mapped)
                {
                    if (used[i]) continue;
                    float m = _accP[i].magnitude * LipMm(i) + _accR[i].magnitude;   // mm + deg (the rotation vectors are in degrees)
                    if (m > bm) { bm = m; best = i; }
                }
                if (best < 0 || bm < 0.05f) break;
                used[best] = true;
                var t = _face[_lip[best]];
                sb.Append(shown++ > 0 ? ", " : "").Append(t != null ? t.name : "?").Append(' ')
                  .Append((_accP[best].magnitude * LipMm(best)).ToString("0.0")).Append(" mm, ")
                  .Append(_accR[best].magnitude.ToString("0.0")).Append(" deg");
            }
            return shown == 0 ? "no bone moved (the poses of the shapes in use are empty)" : sb.ToString();
        }

        // 0.11.11: builds the map's shapes in her mouth's own frame (left / up / forward from her neutral lip corners and lips), sized by her
        // mouth width: millimetre moves of the lip, corner and cheek bones, and a rotation only on the jaw (jawOpen: the survey's widest
        // jaw opening, the lower lips and corners carried round the jaw's pivot when they are not its children; mouthClose: minus that on
        // the lips). 0.9.9-0.11.10 copied whole frames of her face clips instead (a fellatio clip for jawOpen, -50 deg on the lower lip for
        // mouthLowerDown): the lips curled and pouted, and only 7 of the 31 shapes got a pose. onlyEmpty: shapes already posed are kept;
        // shape >= 0: just that one. Returns the number of shapes built. A starting point to refine by eye in the editor.
        public int Seed(bool onlyEmpty, int shape, out string report)
        {
            report = "";
            if (!LipReady) { report = "her face survey is not done"; return 0; }
            int nl = _lip.Length;
            var side = new int[nl];  // -1 right, 0 centre, 1 left
            var cls = new int[nl];   // 0 jaw, 1 upper lip, 2 lower lip, 3 corner, 4 cheek, 5 other (tongue, teeth, nose)
            int jaw = -1, upC = -1, loC = -1, cL = -1, cR = -1;
            for (int i = 0; i < nl; i++)
            {
                string n = _face[_lip[i]].name.ToLowerInvariant();
                side[i] = n.Contains("left") || n.EndsWith("_l") ? 1 : n.Contains("right") || n.EndsWith("_r") ? -1 : 0;
                bool tongue = n.Contains("tongue") || n.Contains("teeth") || n.Contains("tooth");
                cls[i] = n.Contains("jaw") ? 0 : tongue ? 5 : n.Contains("lip") ? (n.Contains("upper") ? 1 : n.Contains("lower") ? 2 : side[i] != 0 ? 3 : 5)
                       : n.Contains("cheek") ? 4 : 5;
                if (cls[i] == 0 && (jaw < 0 || _lip[i] == _jaw)) jaw = i;
                if (cls[i] == 1 && side[i] == 0 && upC < 0) upC = i;
                if (cls[i] == 2 && side[i] == 0 && loC < 0) loC = i;
                if (cls[i] == 3 && side[i] == 1 && cL < 0) cL = i;
                if (cls[i] == 3 && side[i] == -1 && cR < 0) cR = i;
            }
            if (cL < 0 || cR < 0 || upC < 0 || loC < 0) { report = "her lip bones were not recognised by name (corners, upper / lower centre)"; return 0; }
            Func<int, Vector3> N = i => _parH[i].MultiplyPoint3x4(_nP[i]);
            Vector3 left = (N(cL) - N(cR)).normalized;
            Vector3 up0 = N(upC) - N(loC);
            Vector3 upv = (up0 - Vector3.Dot(up0, left) * left).normalized;
            Vector3 fwd = Vector3.Cross(left, upv).normalized;
            Vector3 mid = 0.25f * (N(cL) + N(cR) + N(upC) + N(loC));
            if (Vector3.Dot(fwd, mid) < 0f) fwd = -fwd;   // the mouth is in front of the head bone's origin
            if (left.sqrMagnitude < 0.5f || upv.sqrMagnitude < 0.5f) { report = "her neutral lip corners / lips coincide"; return 0; }
            float mmH = _head.TransformVector(Vector3.right).magnitude * 1000f;
            float width = (N(cL) - N(cR)).magnitude * mmH;
            float sc = Mathf.Clamp(width / 45f, 0.6f, 1.6f);   // the moves below are for a 45 mm wide mouth
            Transform jt = jaw >= 0 ? _face[_lip[jaw]] : null;

            var acc = new Dictionary<int, Vector3>();
            // a move in mm in the mouth's frame (x her left, y up, z forward) on every bone sel picks
            Action<Func<int, bool>, Func<int, Vector3>> add = (sel, v) =>
            {
                for (int i = 0; i < nl; i++)
                {
                    if (!sel(i)) continue;
                    Vector3 m = v(i);
                    Vector3 h = (left * m.x + upv * m.y + fwd * m.z) * (sc / mmH);
                    Vector3 d; acc.TryGetValue(i, out d);
                    acc[i] = d + _parH[i].inverse.MultiplyVector(h);
                }
            };

            int done = 0;
            var sb = new StringBuilder();
            for (int s = 0; s < LipSync.K; s++)
            {
                if (shape >= 0 && s != shape) continue;
                if (onlyEmpty && LipSync.Map[s].Count > 0) continue;
                string nm = LipSync.Shapes[s];
                int sd = nm.EndsWith("Left") ? 1 : nm.EndsWith("Right") ? -1 : 0;
                acc.Clear();
                if (nm == "jawOpen" || nm == "mouthClose")
                {
                    if (nm == "mouthClose" && LipSync.Map[LipSync.JawOpen].Count == 0) continue;   // built from jawOpen
                    LipSync.Map[s].Clear();
                    if (nm == "mouthClose")
                    {
                        // the lips back together while the jaw is open: minus jawOpen's lower lips and corners (the jaw stays open; the
                        // player clamps mouthClose to jawOpen so a closed jaw never pushes the lips past her neutral mouth)
                        foreach (var e in LipSync.Map[LipSync.JawOpen].ToArray())
                        {
                            int li = -1;
                            for (int i = 0; i < nl; i++) if (_face[_lip[i]].name == e.Bone) { li = i; break; }
                            if (li < 0 || (cls[li] != 2 && cls[li] != 3)) continue;
                            var ne = LipSync.Find(s, e.Bone, true); ne.P = -e.P; ne.R = -e.R;
                        }
                    }
                    else if (jt != null && _jawDeg >= 2f)
                    {
                        Vector3 rj = LipSync.Log(_jawD) * Mathf.Min(1f, 25f / _jawDeg);   // the jaw's own axes, degrees
                        var je = LipSync.Find(s, jt.name, true); je.P = Vector3.zero; je.R = rj;
                        Quaternion qj = _parH[jaw].rotation * _nQ[jaw];
                        Vector3 rh = qj * rj;                                              // the same turn in the head's frame
                        Vector3 piv = N(jaw);
                        for (int i = 0; i < nl; i++)
                        {
                            float f = cls[i] == 2 ? 1f : cls[i] == 3 ? 0.5f : 0f;
                            if (f <= 0f || _face[_lip[i]].IsChildOf(jt)) continue;   // the jaw's children follow it already
                            Vector3 p0 = N(i);
                            Vector3 dh = LipSync.Exp(rh * f) * (p0 - piv) + piv - p0;
                            Quaternion qi = _parH[i].rotation * _nQ[i];
                            var e = LipSync.Find(s, _face[_lip[i]].name, true);
                            e.P = _parH[i].inverse.MultiplyVector(dh);
                            e.R = Quaternion.Inverse(qi) * (rh * f);
                        }
                    }
                    else
                    {
                        // no jaw turn in the survey: the lower lips drop instead
                        add(i => cls[i] == 2, i => new Vector3(0f, -9f, -1f));
                        add(i => cls[i] == 3, i => new Vector3(0f, -4f, 0f));
                    }
                }
                else
                {
                    Func<int, bool> U = i => cls[i] == 1, Lo = i => cls[i] == 2, C = i => cls[i] == 3, Ch = i => cls[i] == 4;
                    Func<int, bool> lips = i => cls[i] >= 1 && cls[i] <= 3;
                    switch (nm)
                    {
                        case "jawForward": add(Lo, i => new Vector3(0f, 0f, 3f)); add(C, i => new Vector3(0f, 0f, 1.5f)); break;
                        case "jawLeft": case "jawRight": add(Lo, i => new Vector3(3f * sd, 0f, 0f)); add(C, i => new Vector3(1.5f * sd, 0f, 0f)); break;
                        case "mouthLeft": case "mouthRight": add(lips, i => new Vector3(4f * sd, 0f, 0f)); break;
                        case "mouthFunnel":
                            add(U, i => new Vector3(-1f * side[i], 1.5f, 3f)); add(Lo, i => new Vector3(-1f * side[i], -1.5f, 3f));
                            add(C, i => new Vector3(-3f * side[i], 0f, 2f)); break;
                        case "mouthPucker":
                            add(U, i => new Vector3(-2f * side[i], -0.5f, 4f)); add(Lo, i => new Vector3(-2f * side[i], 0.5f, 4f));
                            add(C, i => new Vector3(-5f * side[i], 0f, 3f)); break;
                        case "mouthSmileLeft": case "mouthSmileRight":
                            add(i => C(i) && side[i] == sd, i => new Vector3(3f * sd, 4f, -1.5f));
                            add(i => (U(i) || Lo(i)) && side[i] == sd, i => new Vector3(1f * sd, U(i) ? 1.5f : 1f, -0.5f));
                            add(i => (U(i) || Lo(i)) && side[i] == 0, i => new Vector3(0.5f * sd, 0.5f, 0f));
                            add(i => Ch(i) && side[i] == sd, i => new Vector3(0.5f * sd, 2.5f, 0.5f)); break;
                        case "mouthFrownLeft": case "mouthFrownRight":
                            add(i => C(i) && side[i] == sd, i => new Vector3(0.5f * sd, -3.5f, 0f));
                            add(i => Lo(i) && side[i] == sd, i => new Vector3(0f, -1f, 0f)); break;
                        case "mouthDimpleLeft": case "mouthDimpleRight":
                            add(i => C(i) && side[i] == sd, i => new Vector3(1.5f * sd, 0f, -2f)); break;
                        case "mouthStretchLeft": case "mouthStretchRight":
                            add(i => C(i) && side[i] == sd, i => new Vector3(3f * sd, -1.5f, -0.5f));
                            add(i => Lo(i) && side[i] == sd, i => new Vector3(1f * sd, -1f, 0f)); break;
                        case "mouthRollLower": add(Lo, i => new Vector3(0f, 1.5f, -2.5f)); break;
                        case "mouthRollUpper": add(U, i => new Vector3(0f, -1.5f, -2.5f)); break;
                        case "mouthShrugLower": add(Lo, i => new Vector3(0f, 2f, 1.5f)); break;
                        case "mouthShrugUpper": add(U, i => new Vector3(0f, 1.5f, 1f)); break;
                        case "mouthPressLeft": case "mouthPressRight":
                            add(i => (U(i) || Lo(i)) && side[i] == sd, i => new Vector3(0f, U(i) ? -1f : 1f, -0.5f));
                            add(i => (U(i) || Lo(i)) && side[i] == 0, i => new Vector3(0f, U(i) ? -0.5f : 0.5f, 0f));
                            add(i => C(i) && side[i] == sd, i => new Vector3(0f, 0f, -0.5f)); break;
                        case "mouthLowerDownLeft": case "mouthLowerDownRight":
                            add(i => Lo(i) && side[i] == sd, i => new Vector3(0f, -3f, 0.5f));
                            add(i => Lo(i) && side[i] == 0, i => new Vector3(0f, -1.5f, 0.3f)); break;
                        case "mouthUpperUpLeft": case "mouthUpperUpRight":
                            add(i => U(i) && side[i] == sd, i => new Vector3(0f, 3f, 0.5f));
                            add(i => U(i) && side[i] == 0, i => new Vector3(0f, 1.5f, 0.3f)); break;
                        case "cheekPuff": add(Ch, i => new Vector3(3f * side[i], 0f, 2f)); add(C, i => new Vector3(0.5f * side[i], 0f, 0f)); break;
                        case "cheekSquintLeft": case "cheekSquintRight": add(i => Ch(i) && side[i] == sd, i => new Vector3(0f, 2f, 0f)); break;
                        default: continue;   // tongueOut: sculpt it
                    }
                    if (acc.Count == 0) continue;
                    LipSync.Map[s].Clear();
                    foreach (var kv in acc) { var e = LipSync.Find(s, _face[_lip[kv.Key]].name, true); e.P = kv.Value; e.R = Vector3.zero; }
                }
                if (LipSync.Map[s].Count == 0) continue;
                done++;
                sb.Append(sb.Length > 0 ? ", " : "").Append(nm);
            }
            if (done > 0) LipSync.Changed();
            report = done == 0 ? "nothing to build" : "built " + done + " shapes for a " + width.ToString("0") + " mm mouth" + (jt != null && _jawDeg >= 2f ? " (jaw up to " + Mathf.Min(_jawDeg, 25f).ToString("0") + " deg)" : " (no jaw turn: the lower lips drop)") + ": " + sb;
            L("lip map " + report);
            return done;
        }

        // ---------------- the game's hooks ----------------

        // After the game's eye reaction (or another hook) fired: watch its face layers for 0.6 s and note which clips came in. A mood whose clip
        // is one of them is left to that hook (it is not layered twice).
        public void Watch(string tag, float now)
        {
            if (_anim == null || _phase != 3 || _watchTag != null) return;
            _watchTag = tag; _watchUntil = now + 0.6f; _watchBase.Clear(); _watchNew.Clear();
            ReadLayers(_watchBase);
        }

        void WatchTick(float now)
        {
            if (_watchTag == null) return;
            var cur = new HashSet<string>();
            ReadLayers(cur);
            foreach (var n in cur) if (!_watchBase.Contains(n)) _watchNew.Add(n);
            if (now < _watchUntil) return;
            if (_watchNew.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var n in _watchNew) { if (sb.Length > 0) sb.Append(", "); sb.Append(n); if (!_hooked.ContainsKey(n)) _hooked[n] = _watchTag; }
                L("the game's " + _watchTag + " plays: " + sb + (Array.Exists(_pick, p => p != null && _watchNew.Contains(p.Name)) ? " (a picked mood clip: left to the game's hook)" : ""));
            }
            _watchTag = null;
        }

        readonly HashSet<string> _nowSet = new HashSet<string>();
        bool PlayingNow(string clip)
        {
            if (_anim == null) return false;
            _nowSet.Clear(); ReadLayers(_nowSet);
            return _nowSet.Contains(clip);
        }

        void ReadLayers(HashSet<string> into)
        {
            try
            {
                int n = _anim.layerCount;
                for (int l = 1; l < n; l++)
                {
                    if (_anim.GetLayerWeight(l) < 0.1f) continue;   // 0.9.0: a layer at weight 0 shows nothing
                    foreach (var ci in _anim.GetCurrentAnimatorClipInfo(l)) if (ci.clip != null && ci.weight > 0.1f) into.Add(ci.clip.name);
                    foreach (var ci in _anim.GetNextAnimatorClipInfo(l)) if (ci.clip != null && ci.weight > 0.1f) into.Add(ci.clip.name);
                }
            }
            catch (Exception) { }
        }
    }
}
