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
            new[] { "tickle", "laugh", "giggle", "smile", "shy", "embarrass", "happy" },
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
        readonly Dictionary<string, string> _hooked = new Dictionary<string, string>();
        string _map = null;
        int _phase, _next;            // 0 not started, 1 surveying, 2 ranking, 3 done
        float _t0, _lastNow;
        bool _sampleBroken, _logged;
        readonly Queue<Track> _build = new Queue<Track>();

        // playback
        Track _cur; int _curMood = -1; float _t, _w, _hold, _env; bool _rel;
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
            L("bound to '" + _root.name + "' (" + n + " transforms, " + _rends.Length + " renderers), " + nf + " face bones under Head_M (" + nb + " with a bind pose; eyeballs and hair left out)");
            return nf > 0;
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
            _phase = 0; _next = 0; _clips.Clear(); _ranked.Clear(); _tracks.Clear(); _build.Clear(); _hooked.Clear();
            for (int i = 0; i < NM; i++) { _pick[i] = null; _pickWhy[i] = null; }
            _anim = null; _go = null; _root = null; _head = null; _ctrl = null; _all = null; _rends = null; _face = null;
            _cur = null; _curMood = -1; _pendMood = -1; _sampleBroken = false; _map = null; _watchTag = null;
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
                    gs[_grp[b]] += s;
                    if (s > top[b]) top[b] = s;
                }
                for (int g = 0; g < NG; g++) if (gs[g] > ci.G[g]) ci.G[g] = gs[g];
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
            if (log || !_logged)
            {
                _logged = true;
                var sb = new StringBuilder("moods: ");
                for (int m = 0; m < NM; m++)
                {
                    if (m > 0) sb.Append(", ");
                    sb.Append(MoodNames[m]).Append(" = ").Append(_pick[m] != null ? "'" + _pick[m].Name + "' (" + _pickWhy[m] + ")" : "none");
                }
                L(sb.ToString());
            }
            // the picked clips' tracks are built ahead (a few samples per frame)
            for (int m = 0; m < NM; m++) if (_pick[m] != null) TrackOf(_pick[m]);
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
            if (m < 0 || m >= NM || _pick[m] == null) { why = "no clip"; return false; }
            string hk;
            if (_hooked.TryGetValue(_pick[m].Name, out hk)) { why = "hook"; return false; }
            if (_cur != null && !_rel && Prio(_curMood) > Prio(m)) { why = "busy"; return false; }
            var tr = TrackOf(_pick[m]);
            if (tr.Failed) { why = "no clip"; return false; }
            if (!tr.Ready) { _pendMood = m; _pendW = weight; _pendHold = hold; _pendUntil = _lastNow + 3f; why = "building"; return false; }
            _cur = tr; _curMood = m; _t = 0f; _w = Mathf.Clamp01(weight); _hold = Mathf.Max(0.1f, hold); _rel = false;
            return true;
        }

        public string CurrentName { get { return _cur != null ? _cur.Ci.Name : ""; } }
        public int CurrentMood { get { return _cur != null ? _curMood : -1; } }

        // Body.ApplyAll for her, after the Animator and before the bone sliders: the face of this frame. mouthFree: she isn't speaking or moaning.
        public void Step(float dt, bool mouthFree, float scale)
        {
            if (_cur == null || _face == null || _anim == null || !_anim.isActiveAndEnabled) return;
            _t += dt;
            const float attack = 0.12f, release = 0.45f;
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
                if (!mouthFree && _grp[b] == GMouth) continue;
                var t = _face[b];
                if (t == null) continue;
                Vector3 p; Quaternion q; t.GetLocalPositionAndRotation(out p, out q);
                if (tr.PosB[k]) p = Vector3.LerpUnclamped(p, Vector3.LerpUnclamped(tr.P[i0 * nb + k], tr.P[i1 * nb + k], f), w);
                if (tr.RotB[k]) q = Quaternion.SlerpUnclamped(q, Quaternion.SlerpUnclamped(tr.R[i0 * nb + k], tr.R[i1 * nb + k], f), w);
                t.SetLocalPositionAndRotation(p, q);
            }
        }

        public void Stop() { _cur = null; _curMood = -1; _pendMood = -1; }

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

        void ReadLayers(HashSet<string> into)
        {
            try
            {
                int n = _anim.layerCount;
                for (int l = 1; l < n; l++)
                {
                    foreach (var ci in _anim.GetCurrentAnimatorClipInfo(l)) if (ci.clip != null && ci.weight > 0.1f) into.Add(ci.clip.name);
                    foreach (var ci in _anim.GetNextAnimatorClipInfo(l)) if (ci.clip != null && ci.weight > 0.1f) into.Add(ci.clip.name);
                }
            }
            catch (Exception) { }
        }
    }
}
