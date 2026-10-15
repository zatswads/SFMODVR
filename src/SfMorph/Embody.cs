using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // Embody-like VR for the partner: view at his eyes, his head hidden, his hands following the VR controllers.
    internal class EmbodySettings
    {
        public bool On;
        public int Hands;          // 0 none, 1 left, 2 right, 3 both
        public bool HideHead;
        public int View;           // 0 the game's camera, 1 snap once to his eyes, 2 follow his eyes (position only)
        public int Partner;        // 0 MovePlayerAndGirl.player, 1 .badGuy
        // 0.8.0: his fingers rest neutral (trigger = index, grip = clasp that stops on her), his elbow swings around her, the rest curl
        public bool NeutralFingers = true, ElbowAvoid = true;
        public float RestCurl = 0.25f;
        // Controller -> his wrist (m, degrees), in the controller's axes. The position is where the wrist sits relative to the
        // tracked pose (Quest Touch, from Embody for VaM); the right hand is the left mirrored. The rotation is added to an
        // automatic hand alignment (fingers along the controller's forward, thumb along its up, palm toward the body's midline;
        // worked out from his finger bones), so zero is the natural grip and the VaM numbers (-90, 90, 0) do not apply here.
        public Vector3 LeftPos = new Vector3(-0.0325f, -0.0379f, -0.1012f), LeftRot = Vector3.zero;
        public Vector3 RightPos = new Vector3(0.0325f, -0.0379f, -0.1012f), RightRot = Vector3.zero;
    }

    internal class Embody
    {
        public string Status = "";
        // 0.8.0 (design-0.8.0 section 6.3): his hands at the CONTROLLER (the IK target), only for arms Embody drives this frame and only
        // while !PhysicsHands.Active. Stub until P3.
        internal static int FillGhost(WorldCap[] into, int start, int max) { return 0; }
        // True while Embody's IK wrote that arm of his this frame (or last frame: callers that run before the apply step see
        // the previous frame's write). False as soon as the arm is given back.
        public bool DrivesArm(bool left)
        {
            var r = _rig;
            if (!_active || _failed || r == null) return false;
            var a = left ? r.L : r.R;
            return a.Written && a.PlacedFrame >= Time.frameCount - 1;
        }

        const float FollowSeconds = 0.1f;   // exponential catch-up of the rig to his eye in Follow
        const float JumpMeters = 0.5f;      // something else moving the rig this far in one frame is logged once
        const float MovedMeters = 0.02f;    // the rig is not where we left it: someone else wrote it
        const float ReachClamp = 0.98f;     // the wrist never asks for more than this share of a straight arm
        const float OverrideMeters = 0.02f; // his wrist is not where we put it by render time: IK or a script overwrote it
        const float HiddenScale = 0.001f;   // Head_M scale while hidden
        const float EyeForward = 0.07f, EyeUp = 0.03f; // eye point from Head_M when there are no eye bones
        const float FollowHz = 6f;          // arm IK target: critically damped follow (zeta 1)
        const float FollowReset = 0.5f;     // the target jumped this far in one frame: follow restarts there
        const float FollowMaxLag = 0.2f;    // the follow never trails the target by more than this
        const float CurlRate = 25f;         // his fingers' catch-up to the grip, per second
        static readonly float[] FingerMax = { 80f, 90f, 60f };   // curl at full grip, proximal / middle / distal (same as the physics hands)
        static readonly float[] ThumbMax = { 20f, 35f, 45f };
        static readonly string[] FingerWord = { "thumb", "index", "middle", "ring", "pinky" };

        // One arm: Shoulder (upper arm joint), Elbow, Wrist, and the finger roots that give the hand its frame.
        class Arm
        {
            public string Side;
            public Transform Shoulder, Elbow, Wrist, Thumb, Pinky, Middle;
            public Quaternion Kinv = Quaternion.identity;   // wrist rotation = hand frame * Kinv
            public bool Calibrated;
            public bool Ready { get { return Shoulder != null && Elbow != null && Wrist != null; } }
            // What the animation had set before our write, and what we wrote: give the pose back only if still ours.
            public Quaternion PreS, PreE, PreW, PostS, PostE, PostW;
            public bool Written;
            public Vector3 Placed;          // where our write put the wrist (world)
            public int PlacedFrame = -1;
            public bool Overridden, OverrideLogged;
            public float Dist = -1f;        // shoulder -> target, m (before the reach clamp)
            public string Note = "";
            // Fingers: 5 chains (thumb, index, middle, ring, pinky) of 3 bones, null when none were found.
            public Finger[] Fingers;
            public string FingerInfo = "", FingerSrc = "";
            public float Curl = -1f;        // the grip as his fingers follow it (smoothed)
            // Follow of the IK target (world): the smoothed point, its velocity, and how far it trails the raw target.
            public bool Follow;
            public Vector3 SmoothT, SmoothV, LastRaw, Lag;
            public bool JumpLogged;
        }

        // One finger of his: three bones, the curl axis in each bone's own frame, and the same give-back bookkeeping as the arm.
        class Finger
        {
            public readonly Transform[] B = new Transform[3];
            public readonly Vector3[] AxisL = new Vector3[3];
            public readonly Quaternion[] Pre = new Quaternion[3], Post = new Quaternion[3];
            public readonly bool[] Written = new bool[3];
        }

        // His bones, found once per partner.
        class Rig
        {
            public GameObject Go;
            public Transform Head, Neck, EyeL, EyeR;
            public readonly Arm L = new Arm { Side = "left" }, R = new Arm { Side = "right" };
            public Vector3 FwdL = Vector3.forward, UpL = Vector3.up;   // his head's forward and up in Head_M's axes
            public string AxesFrom = "";
        }

        readonly Dictionary<GameObject, Rig> _rigs = new Dictionary<GameObject, Rig>();
        Rig _rig;
        // the partner rig we embody (null when off): his hands follow your controllers then, so HerBody lets them push her
        public Transform PartnerRoot { get { return _rig != null && _rig.Go != null ? _rig.Go.transform : null; } }
        EmbodySettings _cur;
        bool _active, _failed, _subscribed, _snapPending;
        int _partnerIdx = -1;
        float _nextRig, _nextStatus;

        // The eye point in his head's frame (so it survives the head being scaled away), his head's directions now.
        Vector3 _eyeRel, _hFwd, _hUp;
        string _eyeFrom = "";

        // View: the rig root we move, and everything we did to it as one rigid motion M (x -> _q*x + _t) so it can be undone.
        Camera _cam;
        Transform _viewRig;
        Quaternion _q = Quaternion.identity;
        Vector3 _t;
        Vector3 _rigPos;                // where we left the rig (compared next frame and before render)
        Quaternion _rigRot = Quaternion.identity;
        int _fight;                     // consecutive frames someone else moved the rig
        bool _jumpLogged, _lateLogged, _snapLogged;
        int _lateUntil;                 // frame until which the view is corrected again right before render
        // His eye smoothed over FollowSeconds (the TARGET is smoothed; every rig move puts the headset exactly on it).
        Vector3 _eyeSmooth;
        bool _eyeInit;
        float _dt = 0.016f, _dtRaw = 0.016f;   // this frame's step (clamped, raw) for the arm follow
        Transform _rootCam;             // the camera the cached rig root was resolved for
        float _nextRoot;
        string _rootWhy = "";

        // Head hiding: the base scale (the game's) and our last write, like Body's trackers.
        Transform _headT;
        Vector3 _headRest, _headLast;
        bool _headHidden;

        // ---- API ----

        // Every frame in the apply step, after Body applies (his pose is final for this frame except LateUpdate scripts).
        public void Update(Character her, List<Character> everyone, EmbodySettings s)
        {
            if (s == null || !s.On)
            {
                if (_active || _failed || Status.Length > 0) Release();
                return;
            }
            if (_failed) return;   // stays off until the user switches it off and on again
            try { Step(everyone, s); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Embody failed, switched off until toggled: " + e);
                try { Release(); } catch { }
                _failed = true;
                Status = "stopped after an error (see the log): " + e.Message;
            }
        }

        // His Head_M while we are hiding it (scaled away), null otherwise: Body leaves it alone meanwhile.
        public Transform HiddenHead { get { return _headHidden ? _headT : null; } }

        // Panel button: turn the view to where he faces and put it on his eyes again.
        public void SnapView() { _snapPending = true; }

        // Gives back the rig's pose, his head scale and his arms (where still ours) and stops writing.
        public void Release()
        {
            if (_subscribed) { Application.onBeforeRender -= BeforeRender; _subscribed = false; }
            try { UndoHead(); } catch { }
            try { UndoView(); } catch { }
            if (_rig != null) { StopArm(_rig.L); StopArm(_rig.R); }
            _rig = null; _cur = null; _cam = null;
            _active = false; _failed = false; _partnerIdx = -1; _nextRig = 0f; _snapPending = false;
            _snapLogged = false; _lateLogged = false; _jumpLogged = false; _lateUntil = 0; _fight = 0;
            _eyeInit = false; _rootCam = null; _nextRoot = 0f;
            Status = "";
        }

        // ---- the frame ----

        void Step(List<Character> everyone, EmbodySettings s)
        {
            float now = Time.unscaledTime;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            _dt = dt; _dtRaw = Time.unscaledDeltaTime;
            if (!_active) { _active = true; _snapPending = true; }
            _cur = s;
            if (!_subscribed) { Application.onBeforeRender += BeforeRender; _subscribed = true; }

            if (_rig != null && _rig.Go == null) { UndoHead(); _rig = null; _nextRig = 0f; } // destroyed: scene change, re-found below
            if (_rig == null || _partnerIdx != s.Partner || now >= _nextRig)
            {
                _nextRig = now + 1f;
                var go = GameRig.Partner(s.Partner);
                if (_rig == null || _partnerIdx != s.Partner || go != _rig.Go) SetPartner(go, s.Partner, everyone);
            }
            string who = s.Partner == 1 ? "BadGuy" : "Player";
            if (_rig == null)
            {
                if (now >= _nextStatus) { _nextStatus = now + 0.25f; Status = "on: no " + who + " in this scene"; }
                return;
            }

            if (_rig.Head == null)
            {
                // No Head_M and no humanoid head: no eye point and nothing to hide, but the hands can still follow the controllers.
                UndoHead(); UndoView();
                HandsStep(s);
                if (now >= _nextStatus)
                {
                    _nextStatus = now + 0.25f;
                    Status = "on: '" + (_rig.Go != null ? _rig.Go.name : who) + "' has no head bone (Head_M/humanoid Head): view and head hiding off";
                }
                return;
            }

            bool viewOn = s.View != 0;
            HeadBegin();                       // his true scale back for a moment, so the eye bones are where the eyes are
            if (viewOn) { EyeNow(dt); ViewStep(s, dt); }
            else UndoView();
            HeadEnd(s.HideHead);
            HandsStep(s);                      // after the view: the controllers ride on the rig we just moved

            if (now >= _nextStatus) { _nextStatus = now + 0.25f; Status = BuildStatus(s, who); }
        }

        void SetPartner(GameObject go, int idx, List<Character> everyone)
        {
            UndoHead();
            if (_rig != null) { StopArm(_rig.L); StopArm(_rig.R); }
            _partnerIdx = idx;
            _snapPending = true;
            if (go == null) { _rig = null; return; }
            Rig r;
            if (!_rigs.TryGetValue(go, out r) || r.Go == null)
            {
                var dead = new List<GameObject>();
                foreach (var k in _rigs.Keys) if (k == null) dead.Add(k);
                foreach (var k in dead) _rigs.Remove(k);
                r = FindRig(go, everyone);
                _rigs[go] = r;
            }
            _rig = r;
        }

        // Before render: right after LateUpdate scripts (FinalIK, DynamicBone) and the headset's tracking update.
        // Only reads and, if something there overwrote what we put, puts it again; nothing else happens here.
        void BeforeRender()
        {
            if (!_active || _failed || _cur == null || _rig == null) return;
            try
            {
                if (_rig.Go == null) return;
                int frame = Time.frameCount;
                // The view first (it moves the rig the controllers ride on), then the arms against where the controllers are now.
                bool corrected = false;
                if (_rig.Head != null && _viewRig != null && _cam != null && _cur.View == 2 && _eyeInit)
                {
                    if ((_viewRig.position - _rigPos).sqrMagnitude > MovedMeters * MovedMeters)
                    {
                        if (!_lateLogged)
                        {
                            _lateLogged = true;
                            Plugin.Log.LogInfo("Embody: something moves the camera rig after our step (LateUpdate?), "
                                + (_viewRig.position - _rigPos).magnitude.ToString("0.00") + " m; correcting the view again right before render. " + GameRig.PovInfo());
                        }
                        _lateUntil = frame + 300;
                    }
                    if (frame < _lateUntil)
                    {
                        var before = _viewRig.position;
                        Translate();
                        corrected = (_viewRig.position - before).sqrMagnitude > 1e-10f;
                        _rigPos = _viewRig.position; _rigRot = _viewRig.rotation;
                    }
                }
                Late(_rig.L, _cur.Hands == 1 || _cur.Hands == 3, VrHands.LeftController, _cur.LeftPos, _cur.LeftRot, frame, corrected);
                Late(_rig.R, _cur.Hands == 2 || _cur.Hands == 3, VrHands.RightController, _cur.RightPos, _cur.RightRot, frame, corrected);
                // the view fix moved the rig the controllers ride on after the physics hands placed themselves: draw them from the corrected rig
                PhysicsHands.PreRenderRefresh();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Embody failed before render, switched off until toggled: " + e);
                _failed = true;
            }
        }

        void Late(Arm a, bool want, Transform ctrl, Vector3 pos, Vector3 rot, int frame, bool corrected)
        {
            if (!want || !a.Ready || ctrl == null || !a.Written || a.PlacedFrame != frame) return;
            float off = (a.Wrist.position - a.Placed).magnitude;
            if (off > OverrideMeters && !a.Overridden)
            {
                a.Overridden = true;
                if (!a.OverrideLogged)
                {
                    a.OverrideLogged = true;
                    Plugin.Log.LogWarning("Embody: his " + a.Side + " arm is overridden after our step (FinalIK/LateUpdate?): the wrist is " + (off * 100f).ToString("0") + " cm from where we put it; placing it again right before render");
                }
            }
            if (corrected || a.Overridden) Place(a, ctrl, pos, rot, false);   // the rig moved, so the controller (and the wrist target) did
            // His fingers are put again when the arm was overridden, or when something rewrote a finger bone after our step.
            if (a.Fingers != null && (a.Overridden || FingersMoved(a))) WriteFingers(a, 0f);
        }

        // ---- finding his bones ----

        Rig FindRig(GameObject go, List<Character> everyone)
        {
            var r = new Rig { Go = go };
            var by = new Dictionary<string, Transform>();
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) if (!by.ContainsKey(t.name) && !VrHands.UnderSf(t)) by[t.name] = t;
            Transform x;
            r.Head = by.TryGetValue("Head_M", out x) ? x : null;
            r.Neck = by.TryGetValue("Neck_M", out x) ? x : null;
            r.EyeL = by.TryGetValue("Eye_L", out x) ? x : null;
            r.EyeR = by.TryGetValue("Eye_R", out x) ? x : null;
            foreach (var a in new[] { r.L, r.R })
            {
                string sd = a.Side == "left" ? "_L" : "_R";
                a.Shoulder = by.TryGetValue("Shoulder" + sd, out x) ? x : null;
                a.Elbow = by.TryGetValue("Elbow" + sd, out x) ? x : null;
                a.Wrist = by.TryGetValue("Wrist" + sd, out x) ? x : null;
            }

            // A humanoid Animator fills in whatever the AdvancedSkeleton names did not give.
            var an = go.GetComponentInChildren<Animator>(true);
            bool human = an != null && an.isHuman;
            string via = "AdvancedSkeleton names";
            if (human)
            {
                bool used = false;
                Func<HumanBodyBones, Transform> hb = b => an.GetBoneTransform(b);
                if (r.Head == null) { r.Head = hb(HumanBodyBones.Head); used = true; }
                if (r.Neck == null) { r.Neck = hb(HumanBodyBones.Neck); used = true; }
                if (r.EyeL == null) r.EyeL = hb(HumanBodyBones.LeftEye);
                if (r.EyeR == null) r.EyeR = hb(HumanBodyBones.RightEye);
                if (r.L.Shoulder == null) { r.L.Shoulder = hb(HumanBodyBones.LeftUpperArm); used = true; }
                if (r.L.Elbow == null) r.L.Elbow = hb(HumanBodyBones.LeftLowerArm);
                if (r.L.Wrist == null) r.L.Wrist = hb(HumanBodyBones.LeftHand);
                if (r.R.Shoulder == null) { r.R.Shoulder = hb(HumanBodyBones.RightUpperArm); used = true; }
                if (r.R.Elbow == null) r.R.Elbow = hb(HumanBodyBones.RightLowerArm);
                if (r.R.Wrist == null) r.R.Wrist = hb(HumanBodyBones.RightHand);
                r.L.Thumb = hb(HumanBodyBones.LeftThumbProximal); r.L.Pinky = hb(HumanBodyBones.LeftLittleProximal); r.L.Middle = hb(HumanBodyBones.LeftMiddleProximal);
                r.R.Thumb = hb(HumanBodyBones.RightThumbProximal); r.R.Pinky = hb(HumanBodyBones.RightLittleProximal); r.R.Middle = hb(HumanBodyBones.RightMiddleProximal);
                if (used) via += " + the humanoid Animator";
            }
            foreach (var a in new[] { r.L, r.R })
            {
                if (a.Wrist == null) continue;
                if (a.Thumb == null) a.Thumb = FindBelow(a.Wrist, "thumb");
                if (a.Pinky == null) a.Pinky = FindBelow(a.Wrist, "pinky") ?? FindBelow(a.Wrist, "little");
                if (a.Middle == null) a.Middle = FindBelow(a.Wrist, "middle");
            }

            // His head's forward and up in Head_M's axes: from the meshes' bind pose (pose independent), else from the
            // eyes, else from his root's facing (a guess).
            Character host = null;
            if (everyone != null && r.Head != null)
                foreach (var c in everyone) if (c != null && c.Bind.ContainsKey(r.Head)) { host = c; break; }
            if (r.Head != null)
            {
                bool fromBind = host != null && BindAxes(r, host.Bind, by);
                if (!fromBind && r.EyeL != null && r.EyeR != null)
                {
                    var up = r.Neck != null ? (r.Head.position - r.Neck.position) : r.Head.up;
                    var f = Vector3.Cross(r.EyeR.position - r.EyeL.position, up);
                    if (f.sqrMagnitude > 1e-8f)
                    {
                        f.Normalize();
                        up = Vector3.Cross(f, Vector3.Cross(up, f)).normalized;
                        r.FwdL = r.Head.InverseTransformDirection(f); r.UpL = r.Head.InverseTransformDirection(up);
                        r.AxesFrom = "the eyes (Eye_R - Eye_L, neck -> head)";
                    }
                }
                if (r.AxesFrom.Length == 0)
                {
                    r.FwdL = r.Head.InverseTransformDirection(go.transform.forward);
                    r.UpL = r.Head.InverseTransformDirection(go.transform.up);
                    r.AxesFrom = "his root's facing (a guess: no bind pose or eyes)";
                }
            }

            foreach (var a in new[] { r.L, r.R }) Calibrate(a);
            foreach (var a in new[] { r.L, r.R })
            {
                try { FindFingers(a, human ? an : null); }
                catch (Exception e) { a.Fingers = null; a.FingerInfo = "search failed: " + e.Message; }
            }

            var sb = new StringBuilder();
            sb.Append("Embody: partner '").Append(go.name).Append("' via ").Append(via).Append(": head ").Append(r.Head != null ? r.Head.name : "MISSING")
              .Append(", neck ").Append(r.Neck != null ? r.Neck.name : "none").Append(", eyes ").Append(r.EyeL != null && r.EyeR != null ? "yes" : "no (eye point = head + " + EyeForward + " forward + " + EyeUp + " up)");
            if (r.Head != null) sb.Append("; head axes from ").Append(r.AxesFrom).Append(", forward ").Append(r.FwdL.ToString("0.00")).Append(" up ").Append(r.UpL.ToString("0.00"));
            foreach (var a in new[] { r.L, r.R })
            {
                sb.Append("; ").Append(a.Side).Append(" arm ").Append(a.Ready ? a.Shoulder.name + "/" + a.Elbow.name + "/" + a.Wrist.name : "MISSING bones");
                if (a.Ready) sb.Append(a.Calibrated ? ", hand frame from " + a.Middle.name + (a.Thumb != null ? "/" + a.Thumb.name : "") + (a.Pinky != null ? "/" + a.Pinky.name : "") : ", no finger bones: wrist axes used as they are");
                if (a.Ready) sb.Append(", fingers ").Append(a.FingerInfo);
            }
            Plugin.Log.LogInfo(sb.ToString());
            return r;
        }

        // ---- his fingers ----

        // A finger word in a raw bone name, only where a word starts (so "ring" is not found in "spring"/"string").
        static bool NameHas(string raw, string word)
        {
            var n = raw.ToLowerInvariant();
            for (int i = n.IndexOf(word, StringComparison.Ordinal); i >= 0; i = n.IndexOf(word, i + 1, StringComparison.Ordinal))
                if (i == 0 || !char.IsLetter(raw[i - 1]) || char.IsUpper(raw[i])) return true;
            return false;
        }

        static bool IsFingerWord(string raw, int f)
        {
            return NameHas(raw, FingerWord[f]) || (f == 4 && NameHas(raw, "little"));
        }

        // The chain of one finger below the wrist: the first bone with the finger's word whose parent has none, then on through the
        // child carrying the word (the one with most bones below it when there are helpers). Null if there is none.
        static List<Transform> FingerChain(Transform wrist, int f)
        {
            Transform root = null;
            var q = new Queue<Transform>();
            q.Enqueue(wrist);
            int guard = 0;
            while (q.Count > 0 && guard++ < 600 && root == null)
            {
                var t = q.Dequeue();
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (c.name.StartsWith("Sf", StringComparison.Ordinal)) continue;
                    if (IsFingerWord(c.name, f) && (t == wrist || !IsFingerWord(t.name, f))) { root = c; break; }
                    q.Enqueue(c);
                }
            }
            if (root == null) return null;
            var chain = new List<Transform> { root };
            for (var t = root; chain.Count < 8;)
            {
                Transform next = null; int most = -1;
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (c.name.StartsWith("Sf", StringComparison.Ordinal) || !IsFingerWord(c.name, f)) continue;
                    int cnt = c.GetComponentsInChildren<Transform>(true).Length;
                    if (cnt > most) { most = cnt; next = c; }
                }
                if (next == null) break;
                chain.Add(next);
                t = next;
            }
            return chain;
        }

        // His finger chains and their curl axes. A humanoid Animator's thumb..little bones are used where it has them, else the chains
        // are found by name; three bones per finger (the three deepest, but a trailing end joint with nothing below it is not one).
        // Curl axis = cross(finger direction, palm normal), so a positive angle moves the tip toward the palm; the palm normal comes
        // from the fingers' curvature when it is clear, else from the hand's chirality (index side of the knuckle line = thumb side).
        static void FindFingers(Arm a, Animator human)
        {
            a.Fingers = null; a.FingerInfo = "none"; a.Curl = -1f;
            if (!a.Ready) { a.FingerInfo = "none (no arm)"; return; }
            bool left = a.Side == "left";
            var fin = new Finger[5];
            var names = new StringBuilder();
            int first = (int)(left ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal);
            int got = 0;
            for (int f = 0; f < 5; f++)
            {
                var fg = new Finger();
                bool ok = false;
                if (human != null)
                {
                    ok = true;
                    for (int j = 0; j < 3; j++)
                    {
                        var b = human.GetBoneTransform((HumanBodyBones)(first + f * 3 + j));
                        if (b == null || !b.IsChildOf(a.Wrist) || b.name.StartsWith("Sf", StringComparison.Ordinal)) { ok = false; break; }
                        fg.B[j] = b;
                    }
                }
                if (!ok)
                {
                    var ch = FingerChain(a.Wrist, f);
                    if (ch == null || ch.Count < 3) continue;
                    int n = ch.Count;
                    int s0 = n >= 4 && ch[n - 1].childCount == 0 ? n - 4 : n - 3;
                    for (int j = 0; j < 3; j++) fg.B[j] = ch[s0 + j];
                }
                fin[f] = fg;
                got++;
                if (names.Length > 0) names.Append(' ');
                names.Append(fg.B[0].name).Append('>').Append(fg.B[1].name).Append('>').Append(fg.B[2].name);
            }
            // The palm frame from the knuckle line: forward = wrist -> mean of the fingers' first bones, across = pinky -> index.
            Vector3 mean = Vector3.zero; int nk = 0; Transform lastK = null, firstK = null;
            for (int f = 1; f < 5; f++) if (fin[f] != null) { mean += fin[f].B[0].position; nk++; if (firstK == null) firstK = fin[f].B[0]; lastK = fin[f].B[0]; }
            if (nk < 2) { a.FingerInfo = "none (" + got + " finger chains found, need two besides the thumb)"; return; }
            var fwd = mean / nk - a.Wrist.position;
            var across = firstK.position - lastK.position;                      // pinky -> index: the thumb side
            if (fwd.sqrMagnitude < 1e-8f || across.sqrMagnitude < 1e-8f) { a.FingerInfo = "none (palm too small)"; return; }
            fwd.Normalize();
            across -= fwd * Vector3.Dot(across, fwd);
            if (across.sqrMagnitude < 1e-8f) { a.FingerInfo = "none (palm too narrow)"; return; }
            across.Normalize();
            var n0 = Vector3.Cross(fwd, across);                               // palm-down normal of a right hand, palm-up of a left one
            var normal = left ? -n0 : n0;
            string how = "chirality";
            float sum = 0f; int nf = 0;
            for (int f = 1; f < 5; f++)
            {
                if (fin[f] == null) continue;
                var d = fin[f].B[1].position - fin[f].B[0].position;
                if (d.sqrMagnitude < 1e-10f) continue;
                d.Normalize();
                var v = fin[f].B[2].position - fin[f].B[0].position;
                v -= d * Vector3.Dot(v, d);
                sum += Vector3.Dot(v, normal); nf++;
            }
            float mm = nf > 0 ? sum / nf * 1000f : 0f;
            if (Mathf.Abs(mm) >= 3f)
            {
                if (mm < 0f) normal = -normal;
                how = "finger curvature " + mm.ToString("0.0") + " mm";
            }
            for (int f = 0; f < 5; f++)
            {
                if (fin[f] == null) continue;
                var d = fin[f].B[1].position - fin[f].B[0].position;
                if (d.sqrMagnitude < 1e-10f) d = fwd;
                d.Normalize();
                var ax = Vector3.Cross(d, normal);
                if (ax.sqrMagnitude < 1e-8f) ax = Vector3.Cross(fwd, normal);
                ax.Normalize();
                for (int j = 0; j < 3; j++) fin[f].AxisL[j] = Quaternion.Inverse(fin[f].B[j].rotation) * ax;
            }
            a.Fingers = fin;
            a.FingerInfo = got + "/5 (palm normal by " + how + "; " + names + ")";
        }

        // The first transform below root (breadth first, so Finger1 before Finger2) whose name contains the word.
        static Transform FindBelow(Transform root, string word)
        {
            var q = new Queue<Transform>();
            q.Enqueue(root);
            int guard = 0;
            while (q.Count > 0 && guard++ < 400)
            {
                var t = q.Dequeue();
                if (t != root && Body.Norm(t.name).Contains(word)) return t;
                for (int i = 0; i < t.childCount; i++) q.Enqueue(t.GetChild(i));
            }
            return null;
        }

        // Directions in the meshes' bind space like Body does (left from the hips, up from pelvis to chest, forward = their
        // cross, checked against the toes), turned into Head_M's local axes by its bind pose.
        static bool BindAxes(Rig r, Dictionary<Transform, Matrix4x4> bind, Dictionary<string, Transform> by)
        {
            Matrix4x4 bh;
            Transform hl, hr, pel, chest;
            if (!bind.TryGetValue(r.Head, out bh)) return false;
            if (!by.TryGetValue("Hip_L", out hl) || !by.TryGetValue("Hip_R", out hr) || !by.TryGetValue("Root_M", out pel) || !by.TryGetValue("Chest_M", out chest)) return false;
            Vector3 pl, pr, pp, pc;
            if (!BindPos(bind, hl, out pl) || !BindPos(bind, hr, out pr) || !BindPos(bind, pel, out pp) || !BindPos(bind, chest, out pc)) return false;
            var left = (pl - pr).normalized;
            var up = (pc - pp).normalized;
            var fwd = Vector3.Cross(up, left).normalized;
            up = Vector3.Cross(left, fwd).normalized;
            Transform toe, ankle;
            Vector3 pt, pa;
            if (by.TryGetValue("Toes_L", out toe) && by.TryGetValue("Ankle_L", out ankle) && BindPos(bind, toe, out pt) && BindPos(bind, ankle, out pa) && Vector3.Dot(pt - pa, fwd) < 0f) fwd = -fwd;
            r.FwdL = bh.MultiplyVector(fwd).normalized;
            r.UpL = bh.MultiplyVector(up).normalized;
            r.AxesFrom = "the bind pose (hips, chest, toes)";
            return true;
        }

        static bool BindPos(Dictionary<Transform, Matrix4x4> bind, Transform t, out Vector3 p)
        {
            Matrix4x4 m;
            if (t != null && bind.TryGetValue(t, out m)) { p = m.inverse.GetColumn(3); return true; }
            p = Vector3.zero;
            return false;
        }

        // The hand frame of a wrist: z along the fingers (wrist -> middle finger root), y toward the thumb (pinky -> thumb root,
        // made square to z). The wrist's rotation for a wanted frame F is F * Kinv, K = the frame's axes in the wrist's own axes.
        // Child positions in the wrist's space do not change with the animation, so this holds in any pose.
        static void Calibrate(Arm a)
        {
            a.Calibrated = false;
            a.Kinv = Quaternion.identity;
            if (!a.Ready || a.Middle == null) return;
            var lz = a.Wrist.InverseTransformPoint(a.Middle.position);
            Vector3 raw;
            if (a.Thumb != null && a.Pinky != null) raw = a.Wrist.InverseTransformPoint(a.Thumb.position) - a.Wrist.InverseTransformPoint(a.Pinky.position);
            else if (a.Thumb != null) raw = a.Wrist.InverseTransformPoint(a.Thumb.position);
            else if (a.Pinky != null) raw = -a.Wrist.InverseTransformPoint(a.Pinky.position);
            else return;
            if (lz.sqrMagnitude < 1e-10f) return;
            lz.Normalize();
            var ly = raw - lz * Vector3.Dot(raw, lz);
            if (ly.sqrMagnitude < 1e-10f) return;
            ly.Normalize();
            a.Kinv = Quaternion.Inverse(Quaternion.LookRotation(lz, ly));
            a.Calibrated = true;
        }

        // ---- head ----

        // Gives the head its true scale for this frame's measurements (adopting a scale someone else set meanwhile).
        void HeadBegin()
        {
            if (_headT != null && _rig != null && _headT != _rig.Head) UndoHead();
            if (_headT == null || !_headHidden) return;
            var s = _headT.localScale;
            if ((s - _headLast).sqrMagnitude > 1e-12f) _headRest = s;   // the game changed it since our write: that is the new base
            _headT.localScale = _headRest;
        }

        void HeadEnd(bool hide)
        {
            if (!hide) { UndoHead(); return; }
            var h = _rig.Head;
            if (h == null) return;
            if (!_headHidden || _headT != h) { _headT = h; _headRest = h.localScale; _headHidden = true; }
            var tgt = _headRest * HiddenScale;
            h.localScale = tgt;
            _headLast = tgt;
        }

        // Back to the game's scale, unless the game has set another one since.
        void UndoHead()
        {
            if (_headT != null && _headHidden && (_headT.localScale - _headLast).sqrMagnitude < 1e-12f) _headT.localScale = _headRest;
            _headT = null; _headHidden = false;
        }

        // ---- view ----

        // The eye point (mid Eye_L/Eye_R, else a fixed offset from Head_M) kept in the head's frame, and his head's directions.
        void EyeNow(float dt)
        {
            var h = _rig.Head;
            _hFwd = h.TransformDirection(_rig.FwdL);
            _hUp = h.TransformDirection(_rig.UpL);
            Vector3 eye;
            if (_rig.EyeL != null && _rig.EyeR != null && ((_rig.EyeL.position + _rig.EyeR.position) * 0.5f - h.position).sqrMagnitude < 0.09f)
            {
                eye = (_rig.EyeL.position + _rig.EyeR.position) * 0.5f;
                _eyeFrom = "Eye_L/Eye_R";
            }
            else
            {
                eye = h.position + _hFwd * EyeForward + _hUp * EyeUp;
                _eyeFrom = "head + offset";
            }
            _eyeRel = Quaternion.Inverse(h.rotation) * (eye - h.position);

            // The target the headset is put on: his eye smoothed once per frame (snapped on a new start, a snap request or a far jump).
            bool snap = !_eyeInit || _snapPending || (eye - _eyeSmooth).sqrMagnitude > 2.25f;
            _eyeSmooth = snap ? eye : Vector3.Lerp(_eyeSmooth, eye, 1f - Mathf.Exp(-dt / FollowSeconds));
            _eyeInit = true;
        }

        Vector3 EyeWorld() { var h = _rig.Head; return h.position + h.rotation * _eyeRel; }

        // The rig root of a camera: the topmost ancestor below the scene root, or vr_Camera_Mov when the camera is under it (and it
        // is not tied to the partner). Never the camera itself when it has a parent: XR tracking rewrites the camera, so its parent is moved.
        static Transform RigRootOf(Transform cam, Transform mov, Transform partner, out string why)
        {
            var t = cam;
            while (t.parent != null && t.parent.parent != null) t = t.parent;
            why = "topmost ancestor below the scene root";
            if (mov != null && mov != cam && cam.IsChildOf(mov)
                && (partner == null || (!partner.IsChildOf(mov) && !mov.IsChildOf(partner))))
            {
                t = mov;
                why = "vr_Camera_Mov, the camera is under it";
            }
            if (t == cam && cam.parent != null)
            {
                t = cam.parent;
                why = "the camera's parent: XR tracking rewrites the camera itself";
            }
            return t;
        }

        // The direction a head or headset faces on the horizontal plane; when it points nearly straight up or down, its up
        // vector (which then points ahead when looking down, behind when looking up) says where it faces.
        static Vector3 Facing(Vector3 fwd, Vector3 up, Vector3 axis)
        {
            var p = Vector3.ProjectOnPlane(fwd, axis);
            if (p.sqrMagnitude < 0.12f)
            {
                var q = Vector3.ProjectOnPlane(up, axis);
                p = Vector3.Dot(fwd, axis) < 0f ? q : -q;
            }
            return p;
        }

        void ViewStep(EmbodySettings s, float dt)
        {
            var cam = GameRig.VrCamera();
            if (cam == null) { _viewRig = null; return; }
            Transform rigRoot;
            if (_viewRig != null && cam.transform == _rootCam && Time.unscaledTime < _nextRoot) rigRoot = _viewRig;   // not looked up every frame
            else
            {
                _rootCam = cam.transform;
                _nextRoot = Time.unscaledTime + 2f;
                rigRoot = RigRootOf(cam.transform, GameRig.PlayerTransform("vr_Camera_Mov"), _rig.Go != null ? _rig.Go.transform : null, out _rootWhy);
            }
            if (rigRoot != _viewRig)
            {
                UndoView();
                _viewRig = rigRoot;
                _q = Quaternion.identity; _t = Vector3.zero;
                _rigPos = rigRoot.position; _rigRot = rigRoot.rotation;
                _snapPending = true; _jumpLogged = false; _lateLogged = false; _fight = 0; _lateUntil = 0;
                int up = 0;
                for (var p = cam.transform; p != rigRoot; p = p.parent) up++;
                var mov = GameRig.PlayerTransform("vr_Camera_Mov");
                var pov = GameRig.PlayerTransform("vr_PovCamera_Location");
                var vrm = GameRig.VrMode();
                Plugin.Log.LogInfo("Embody: VR camera '" + GameRig.PathOf(cam.transform) + "' (" + GameRig.VrCameraSource + "); moving the rig root '" + GameRig.PathOf(rigRoot)
                    + "' (" + _rootWhy + "), " + up + " level" + (up == 1 ? "" : "s") + " above the camera. [VR Mode] = " + (vrm != null ? GameRig.PathOf(vrm.transform) : "not found")
                    + "; vr_Camera_Mov = " + GameRig.PathOf(mov) + (mov != null && (mov == rigRoot || rigRoot.IsChildOf(mov) || mov.IsChildOf(rigRoot)) ? " (the same branch as the rig)" : "")
                    + "; vr_PovCamera_Location = " + GameRig.PathOf(pov) + "; " + GameRig.PovInfo());
            }
            _cam = cam;

            // Has something else written the rig since our last write? Then our earlier shifts are no longer reliably part of it:
            // forget them (a restore then only undoes what we add from here) and, if it keeps happening, stop smoothing.
            float jump = (rigRoot.position - _rigPos).magnitude;
            if (jump > MovedMeters || Quaternion.Angle(rigRoot.rotation, _rigRot) > 1f)
            {
                _fight++;
                _q = Quaternion.identity; _t = Vector3.zero;
                if (jump > JumpMeters && !_jumpLogged)
                {
                    _jumpLogged = true;
                    Plugin.Log.LogInfo("Embody: something else moved the camera rig " + jump.ToString("0.00") + " m in one frame (not us). " + GameRig.PovInfo()
                        + "; the game's own POV may be at work. Following it from here.");
                }
            }
            else _fight = 0;

            if (_snapPending)
            {
                _snapPending = false;
                var rup = rigRoot.up;
                var hf = Facing(cam.transform.forward, cam.transform.up, rup);
                var tf = Facing(_hFwd, _hUp, rup);
                float yaw = 0f;
                if (hf.sqrMagnitude > 1e-6f && tf.sqrMagnitude > 1e-6f)
                {
                    yaw = Vector3.SignedAngle(hf, tf, rup);
                    // About the headset, so it stays where it is while the world turns around it.
                    Turn(Quaternion.AngleAxis(yaw, rup), cam.transform.position);
                }
                _eyeSmooth = EyeWorld(); _eyeInit = true;   // a new start: no smoothing from the old position
                Translate();
                if (!_snapLogged)
                {
                    _snapLogged = true;
                    Plugin.Log.LogInfo("Embody: snapped the view: turned " + yaw.ToString("0") + " deg to his head's yaw and put the headset on his eye point (" + _eyeFrom + "), head axes from " + _rig.AxesFrom);
                }
            }
            else if (s.View == 2)
            {
                Translate();   // the smoothing is in the target (_eyeSmooth), so this holds even when the game moves the rig too
            }
            _rigPos = rigRoot.position; _rigRot = rigRoot.rotation;
        }

        // Moves the rig so the headset ends up on the smoothed eye target (position only: no rotation writes).
        void Translate()
        {
            var d = _eyeSmooth - _cam.transform.position;
            _viewRig.position += d;
            _t += d;
        }

        void Turn(Quaternion r, Vector3 pivot)
        {
            _viewRig.position = pivot + r * (_viewRig.position - pivot);
            _viewRig.rotation = r * _viewRig.rotation;
            _q = r * _q;
            _t = pivot + r * (_t - pivot);
        }

        // Undoes our net motion of the rig (applied to the pose it has now, so what the game moved meanwhile stays).
        void UndoView()
        {
            if (_viewRig != null && (_t.sqrMagnitude > 1e-10f || Quaternion.Angle(_q, Quaternion.identity) > 0.01f))
            {
                var qi = Quaternion.Inverse(_q);
                _viewRig.position = qi * (_viewRig.position - _t);
                _viewRig.rotation = qi * _viewRig.rotation;
                Plugin.Log.LogInfo("Embody: gave the camera rig '" + GameRig.PathOf(_viewRig) + "' its pose back");
            }
            _viewRig = null;
            _eyeInit = false;
            _q = Quaternion.identity; _t = Vector3.zero;
        }

        // ---- hands ----

        void HandsStep(EmbodySettings s)
        {
            DoArm(_rig.L, s.Hands == 1 || s.Hands == 3, VrHands.LeftController, s.LeftPos, s.LeftRot);
            DoArm(_rig.R, s.Hands == 2 || s.Hands == 3, VrHands.RightController, s.RightPos, s.RightRot);
        }

        void DoArm(Arm a, bool want, Transform ctrl, Vector3 pos, Vector3 rot)
        {
            if (!want) { StopArm(a); a.Note = ""; return; }
            if (!a.Ready) { a.Note = "no arm bones"; return; }
            if (ctrl == null) { StopArm(a); a.Note = "no controller found"; return; }
            a.Note = "";
            Place(a, ctrl, pos, rot, true);
            if (a.Fingers != null && a.PlacedFrame == Time.frameCount) WriteFingers(a, _dt);
        }

        // Analytic two-bone IK: Shoulder -> Elbow -> Wrist reaches the controller's wrist point. The elbow stays on the side the
        // animation has it (the pole is its current offset from the shoulder-target line); the wrist takes the controller's
        // rotation. World rotations on Shoulder and Elbow (their helper bones below carry on), then the wrist.
        void Place(Arm a, Transform ctrl, Vector3 offPos, Vector3 offRot, bool save)
        {
            Vector3 A = a.Shoulder.position, B = a.Elbow.position, C = a.Wrist.position;
            float l1 = (B - A).magnitude, l2 = (C - B).magnitude;
            if (l1 < 1e-4f || l2 < 1e-4f) { a.Note = "arm has no length"; return; }
            // With the physics hands on, his hand stops where the (hidden) physics hand stopped: compose its displacement in.
            Vector3 dp = Vector3.zero;
            Quaternion dq = Quaternion.identity;
            if (PhysicsHands.Active)
            {
                if (a.Side == "left") { dp = PhysicsHands.LeftDp; dq = PhysicsHands.LeftDq; }
                else { dp = PhysicsHands.RightDp; dq = PhysicsHands.RightDq; }
            }
            var cq = dq * ctrl.rotation;
            var raw = ctrl.position + dp + cq * offPos;
            // The target trails the controller a little (critically damped, 6 Hz): wrist and elbow lag on fast moves. Advanced once per
            // frame (save); the pre-render pass keeps the same trail as an offset on the new raw target.
            var T = save ? FollowStep(a, raw) : raw + a.Lag;
            var tq = cq * Quaternion.Euler(offRot) * a.Kinv;

            var toT = T - A;
            float dist = toT.magnitude;
            a.Dist = dist;
            float d = Mathf.Clamp(dist, Mathf.Abs(l1 - l2) * 1.02f + 0.01f, (l1 + l2) * ReachClamp);
            var dir = dist > 1e-5f ? toT / dist : (C - A).normalized;
            var Tc = A + dir * d;

            float cosA = Mathf.Clamp((l1 * l1 + d * d - l2 * l2) / (2f * l1 * d), -1f, 1f);
            float sinA = Mathf.Sqrt(1f - cosA * cosA);
            var pole = (B - A) - dir * Vector3.Dot(B - A, dir);
            if (pole.sqrMagnitude < 1e-6f) pole = Vector3.down - dir * Vector3.Dot(Vector3.down, dir);   // a straight arm: elbow down
            if (pole.sqrMagnitude < 1e-6f) pole = Vector3.Cross(dir, Vector3.right);
            pole.Normalize();
            var B2 = A + dir * (l1 * cosA) + pole * (l1 * sinA);

            if (save)
            {
                a.PreS = a.Shoulder.localRotation; a.PreE = a.Elbow.localRotation; a.PreW = a.Wrist.localRotation;
            }
            a.Shoulder.rotation = FromTo(B - A, B2 - A) * a.Shoulder.rotation;
            B = a.Elbow.position; C = a.Wrist.position;                 // moved with the shoulder
            a.Elbow.rotation = FromTo(C - B, Tc - B) * a.Elbow.rotation;
            a.Wrist.rotation = tq;
            if (save)
            {
                a.PostS = a.Shoulder.localRotation; a.PostE = a.Elbow.localRotation; a.PostW = a.Wrist.localRotation;
                a.Written = true;
            }
            a.Placed = a.Wrist.position;
            a.PlacedFrame = Time.frameCount;
        }

        // Exact critically damped step toward `raw` (zeta 1, FollowHz) over this frame, reset on a first frame, a hitch or a jump.
        Vector3 FollowStep(Arm a, Vector3 raw)
        {
            float jump = a.Follow ? (raw - a.LastRaw).magnitude : 0f;
            if (!a.Follow || jump > FollowReset || _dtRaw > 0.25f)
            {
                if (a.Follow && jump > FollowReset && !a.JumpLogged)
                {
                    a.JumpLogged = true;
                    Plugin.Log.LogInfo("Embody: his " + a.Side + " arm target moved " + jump.ToString("0.00") + " m in one frame; the arm follow restarts there (logged once per arm)");
                }
                a.Follow = true; a.SmoothT = raw; a.SmoothV = Vector3.zero;
            }
            else
            {
                float w = 2f * Mathf.PI * FollowHz;
                float dt = Mathf.Max(_dt, 1e-4f);
                var e = a.SmoothT - raw;
                var c2 = a.SmoothV + w * e;
                float ex = Mathf.Exp(-w * dt);
                a.SmoothT = raw + (e + c2 * dt) * ex;
                a.SmoothV = (a.SmoothV - w * c2 * dt) * ex;
                var lag = a.SmoothT - raw;
                if (lag.sqrMagnitude > FollowMaxLag * FollowMaxLag)
                {
                    lag = lag.normalized * FollowMaxLag;
                    a.SmoothT = raw + lag;
                    a.SmoothV = Vector3.zero;
                }
            }
            a.LastRaw = raw;
            a.Lag = a.SmoothT - raw;
            return a.SmoothT;
        }

        // ---- his fingers ----

        // True if any finger bone is not where our last write left it (something wrote it after our step).
        static bool FingersMoved(Arm a)
        {
            if (a.Fingers == null || !a.Written) return false;
            foreach (var fg in a.Fingers)
            {
                if (fg == null) continue;
                for (int j = 0; j < 3; j++)
                    if (fg.Written[j] && fg.B[j] != null && Quaternion.Angle(fg.B[j].localRotation, fg.Post[j]) > 0.5f) return true;
            }
            return false;
        }

        // Curls his fingers on top of the pose the animation gave them: by the simulated fingers of the physics hand when it has them
        // (its curl angle per bone, applied about his own curl axis), else by the grip. dt 0: no smoothing step (the pre-render pass).
        void WriteFingers(Arm a, float dt)
        {
            bool left = a.Side == "left";
            float grip = Mathf.Clamp01(left ? Grip.Left : Grip.Right);
            if (float.IsNaN(grip)) grip = 0f;
            if (a.Curl < 0f) a.Curl = grip;
            else if (dt > 0f) a.Curl += (grip - a.Curl) * (1f - Mathf.Exp(-CurlRate * dt));

            float[] curl = left ? PhysicsHands.LeftFingerCurlDeg : PhysicsHands.RightFingerCurlDeg;
            bool useSim = PhysicsHands.Active && (left ? PhysicsHands.LeftFingersValid : PhysicsHands.RightFingersValid)
                          && curl != null && curl.Length >= 15;
            string src = useSim ? "simulated" : "grip";
            if (src != a.FingerSrc)
            {
                a.FingerSrc = src;
                Plugin.Log.LogInfo("Embody: his " + a.Side + " fingers now follow " + (useSim ? "the physics hand's simulated fingers" : "the controller grip (Grip." + (left ? "Left" : "Right") + ", " + Grip.Source + ")")
                    + (PhysicsHands.Active && !useSim ? " [its fingers are not valid]" : ""));
            }

            for (int f = 0; f < 5; f++)
            {
                var fg = a.Fingers[f];
                if (fg == null) continue;
                for (int j = 0; j < 3; j++)
                {
                    var b = fg.B[j];
                    if (b == null) continue;
                    float ang = (f == 0 ? ThumbMax[j] : FingerMax[j]) * a.Curl;
                    if (useSim)
                    {
                        float sa = curl[f * 3 + j];   // signed twist about the curl axis: a sideways swing or a backward bend is not a curl
                        if (!float.IsNaN(sa) && !float.IsInfinity(sa)) ang = Mathf.Clamp(sa, -15f, 120f);
                    }
                    var cur = b.localRotation;
                    // Our last write still there: the animation did not write this bone, so its pose is the one from before our write.
                    var baseQ = fg.Written[j] && Quaternion.Angle(cur, fg.Post[j]) < 0.05f ? fg.Pre[j] : cur;
                    fg.Pre[j] = baseQ;
                    var q = baseQ * Quaternion.AngleAxis(ang, fg.AxisL[j]);
                    b.localRotation = q;
                    fg.Post[j] = q;
                    fg.Written[j] = true;
                }
            }
        }

        // Gives his finger bones back where still ours.
        static void StopFingers(Arm a)
        {
            if (a.Fingers == null) return;
            foreach (var fg in a.Fingers)
            {
                if (fg == null) continue;
                for (int j = 0; j < 3; j++)
                {
                    if (fg.Written[j] && fg.B[j] != null && Quaternion.Angle(fg.B[j].localRotation, fg.Post[j]) < 0.05f) fg.B[j].localRotation = fg.Pre[j];
                    fg.Written[j] = false;
                }
            }
            a.Curl = -1f; a.FingerSrc = "";
        }

        static Quaternion FromTo(Vector3 a, Vector3 b)
        {
            if (a.sqrMagnitude < 1e-10f || b.sqrMagnitude < 1e-10f) return Quaternion.identity;
            return Quaternion.FromToRotation(a, b);
        }

        // Animation rewrites his arm every frame, so there is normally nothing to give back; if it did not (no clip drives
        // the bone) and the pose is still the one we wrote, the pose from before our write returns.
        void StopArm(Arm a)
        {
            if (a.Written && a.Ready)
            {
                if (Quaternion.Angle(a.Shoulder.localRotation, a.PostS) < 0.01f) a.Shoulder.localRotation = a.PreS;
                if (Quaternion.Angle(a.Elbow.localRotation, a.PostE) < 0.01f) a.Elbow.localRotation = a.PreE;
                if (Quaternion.Angle(a.Wrist.localRotation, a.PostW) < 0.01f) a.Wrist.localRotation = a.PreW;
            }
            StopFingers(a);
            a.Written = false; a.PlacedFrame = -1; a.Overridden = false; a.Dist = -1f;
            a.Follow = false; a.Lag = Vector3.zero;
        }

        // ---- status line for the panel ----

        string BuildStatus(EmbodySettings s, string who)
        {
            var sb = new StringBuilder();
            sb.Append("on: ").Append(_rig.Go != null ? _rig.Go.name : who);
            if (s.View == 0) sb.Append(", view: the game's camera");
            else if (_viewRig == null) sb.Append(", view: no VR camera found");
            else
            {
                sb.Append(s.View == 1 ? ", view snapped" : ", view follow").Append(" (rig '");
                var p = _viewRig.parent;
                if (p != null) sb.Append(p.name).Append('/');
                sb.Append(_viewRig.name).Append("')");
                if (_fight >= 2 || Time.frameCount < _lateUntil) sb.Append(" [the game also moves the rig; ").Append(GameRig.PovInfo()).Append(']');
            }
            sb.Append(s.HideHead ? (_headHidden ? ", head hidden" : ", head: no Head_M") : ", head shown");
            string[] hn = { "none", "left", "right", "both" };
            sb.Append(", hands ").Append(hn[Mathf.Clamp(s.Hands, 0, 3)]);
            bool l = s.Hands == 1 || s.Hands == 3, r = s.Hands == 2 || s.Hands == 3;
            if (l || r)
            {
                sb.Append(" (");
                if (l) ArmStatus(sb, _rig.L);
                if (l && r) sb.Append(", ");
                if (r) ArmStatus(sb, _rig.R);
                sb.Append(')');
            }
            return sb.ToString();
        }

        static void ArmStatus(StringBuilder sb, Arm a)
        {
            sb.Append(a.Side);
            if (a.Note.Length > 0) sb.Append(": ").Append(a.Note);
            else if (a.Dist >= 0f)
            {
                sb.Append(" target ").Append(a.Dist.ToString("0.00")).Append(" m");
                if (a.Overridden) sb.Append(", overridden by the game's IK or a script, placed again before render");
                if (a.Fingers != null && a.FingerSrc.Length > 0) sb.Append(", fingers ").Append(a.FingerSrc);
            }
        }
    }
}
