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
        // 0.8.0: his fingers rest neutral (the grab button closes the whole hand and it stops on her), his elbow swings around her, the rest curl
        public bool NeutralFingers = true, ElbowAvoid = true;
        public float RestCurl = 0.25f;
        // how soft her body is (the Physics tab's Softness, 0..1): the clasp sinks past first contact by ClaspGive * (0.5 + Softness).
        // Body.cs fills it next to RestCurl (or HerBody.Softness once P2 publishes it).
        public float Softness = 0.5f;
        // whether the soft zones run (the Physics tab): hands may sink 1-3 cm into soft parts then, else 0.5 cm like firm ones (design 13.3a).
        // Body.cs: `_emb.SoftZonesOn = physics;` (true until then).
        public bool SoftZonesOn = true;
        // 0.8.0 (user): what a snap turns the view to (0 his head's yaw, 1 his chest's) and manual corrections applied on every snap
        // (degrees: yaw about the rig's up, + turns the view right; pitch about the headset's right, + nose up; roll about its forward)
        public int ViewAlign = 1;
        public float ViewYaw, ViewPitch, ViewRoll;
        // 0.11.7 (user): where the view sits relative to his eye point, in his head's axes (m: x right, y up, z forward)
        public Vector3 ViewOffset;
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
        static Embody _inst;
        public Embody() { _inst = this; }

        // 0.8.0 (design-0.8.0 section 6.3): his hands at the CONTROLLER (the IK target), only for arms Embody drives this frame and only
        // while !PhysicsHands.Active (the physics ghost stands for the same hand otherwise). HerBody.Update calls it before Embody's
        // own step, so it reads the previous step's finger curl and trail (a frame of lag at most) and this frame's controller.
        internal static int FillGhost(WorldCap[] into, int start, int max)
        {
            var e = _inst;
            if (e == null || into == null || max <= 0 || start < 0) return 0;
            try { return e.Ghost(into, start, max); }
            catch (Exception ex)
            {
                if (!e._ghostErr) { e._ghostErr = true; Plugin.Log.LogWarning("Embody: ghost capsules failed (logged once): " + ex.Message); }
                return 0;
            }
        }
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
        // 0.8.0 commands (the same numbers as the physics hands, design 5.1): rest curl per finger at RestCurl 0.25, the followers' share
        // of the touching fingers' average curl (DynamicLimbs: 0.5 index/pinky, 0.75 middle/ring), the ease (15/s, at most 8/s)
        static readonly float[] RestCurlF = { 0.15f, 0.20f, 0.25f, 0.30f, 0.35f };
        static readonly float[] FollowerK = { 0f, 0.5f, 0.75f, 0.75f, 0.5f };
        const float EaseRate = 15f, EaseMax = 8f;
        const float FanR = 0.010f;          // finger probe radius (m) of the analytic fan
        const float GhostFingerR = 0.009f, GhostThumbR = 0.010f, GhostPalmR = 0.015f;
        // elbow swivel: arm capsule radii (m), how far the forearm capsule runs toward the wrist (the hand itself may touch her),
        // the penetration that starts a search / that is good enough / that keeps the current swivel, the ease (1/s)
        const float UpperArmR = 0.045f, ForearmR = 0.035f, ForearmShare = 0.7f;
        const float SwivelStart = 0.010f, SwivelOk = 0.010f, SwivelKeep = 0.015f, SwivelEase = 8f, SwivelRetry = 0.25f;
        static readonly float[] SwivelDeg = { 0f, 15f, 30f, 45f, 60f, 90f, 120f };

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
            // 0.8.0 neutral fingers: the palm normal and each finger root in the wrist's frame (metres), per-finger commands as eased
            public Vector3 PalmNL;
            public Vector3 AcrossL;          // 0.11.7: pinky -> index across the knuckles in the wrist's frame (the thumb's opposition axis)
            public int LiveNeutralN;         // 0.11.7: fingers whose neutral pose was straightened from the animation's pose (no bind pose)
            public readonly Vector3[] RootW = new Vector3[5];
            public readonly float[] FCurl = new float[5];
            public bool FInit;
            public int NeutralN;            // fingers with a neutral base from the bind pose
            public string NeutralRend = "", NeutralDiag = "";
            public bool WasStopped;         // a finger stopped on her last step (counts stop EVENTS, not frames)
            public int Stops, SwN;          // 10 s diagnostics: clasp stops, frames with a swivel
            public float SwSum, SwMax;
            // 0.8.0 elbow swivel (degrees about the shoulder -> target line): applied, target; where we put the elbow, what we applied
            // to the pole there (so a pass that finds the pole already swivelled can take it out again), the next search time
            public float Swivel, SwivelT, Applied, NextSearch;
            public Vector3 PlacedElbow;
            // 0.8.0 hand stop at her skin: the smoothed shift added to the wrist target (and its velocity), 10 s diagnostics
            public Vector3 Stop, StopV;
            public bool StopInit, StopLogged;
            public int StopN; public float StopSum, StopMax; public string StopSeg = "";
            public bool SwivelLogged;
            public string GhostBy; public int GhostIdx = -1;
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
            // 0.8.0 neutral pose from the bind pose (null = the animation's pose is the base): rotation of each bone relative to the
            // previous one in the chain (the wrist for the first), the joints' origins (+ the tip) and rotations in the wrist's frame
            public Quaternion[] Neutral;
            public Vector3[] NP;            // 4: B0, B1, B2, tip (metres)
            public Quaternion[] NQ;         // 3: neutral rotations in the wrist's frame
            public FingerFan Fan;
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
        float _nextRig, _nextStatus, _nextStat;
        bool _ghostErr;
        readonly HashSet<string> _claspLogged = new HashSet<string>();

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
            _claspLogged.Clear(); _nextStat = 0f;
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
                HerBody.RefreshShapes();   // test his hand (stop, clasp, swivel) against her yielded skin, not the animation's
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
            HerBody.RefreshShapes();           // her skin where her limb IS after Overlap's yield (the physics hands' swivel too)
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
                try { FindFingers(a, human ? an : null, go.transform); }
                catch (Exception e) { a.Fingers = null; a.FingerInfo = "search failed: " + e.Message; }
            }
            LogNeutral(r, go);

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
        static void FindFingers(Arm a, Animator human, Transform root)
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
                var ax = Vector3.Cross(d, f == 0 ? ThumbToward(normal, across) : normal);
                if (ax.sqrMagnitude < 1e-8f) ax = Vector3.Cross(fwd, normal);
                ax.Normalize();
                for (int j = 0; j < 3; j++) fin[f].AxisL[j] = Quaternion.Inverse(fin[f].B[j].rotation) * ax;
            }
            var wi = Quaternion.Inverse(a.Wrist.rotation);
            a.PalmNL = wi * normal;
            a.AcrossL = wi * across;
            for (int f = 0; f < 5; f++) if (fin[f] != null) a.RootW[f] = wi * (fin[f].B[0].position - a.Wrist.position);
            a.Fingers = fin;
            a.FingerInfo = got + "/5 (palm normal by " + how + "; " + names + ")";
            BuildNeutral(a, root);
            NeutralFromPose(a);
        }

        // 0.11.7 (user: on a grab the thumb bends unrealistically): the thumb does not curl like a finger, toward the palm normal, but
        // across the palm toward the fingers (opposition): its curl moves the tip halfway between the palm normal and the pinky side.
        static Vector3 ThumbToward(Vector3 normal, Vector3 across)
        {
            var t = normal.normalized - across.normalized;
            return t.sqrMagnitude > 1e-6f ? t.normalized : normal;
        }

        // 0.11.7 (user: the hand no longer rests neutral, it keeps the animation's pose; the 0.11.4 log: "no skinned renderer carries his
        // finger bones"): without a bind pose, the neutral hand is the animation's hand with every joint's curl taken out, measured
        // geometrically: each bone is turned about its own curl axis until it lines up with the bone above it (the first joint with the
        // palm's direction, the thumb's first joint left as it is). The same data as BuildNeutral, so the rest curl, the grab button
        // and the clasp stop on her skin all work on it. Taken once, when his fingers are found.
        static void NeutralFromPose(Arm a)
        {
            a.LiveNeutralN = 0;
            if (a.Fingers == null || a.Wrist == null) return;
            var wq = a.Wrist.rotation; var wi = Quaternion.Inverse(wq); var wp = a.Wrist.position;
            if (!Finite(wq)) return;
            // the palm's direction: wrist -> the mean of the four finger roots
            Vector3 mean = Vector3.zero; int nk = 0;
            for (int f = 1; f < 5; f++) if (a.Fingers[f] != null) { mean += a.Fingers[f].B[0].position; nk++; }
            if (nk == 0) return;
            var palmFwd = mean / nk - wp;
            if (palmFwd.sqrMagnitude < 1e-8f) return;
            palmFwd.Normalize();
            for (int f = 0; f < 5; f++)
            {
                var fg = a.Fingers[f];
                if (fg == null || fg.Neutral != null) continue;
                var pos = new Vector3[4];
                for (int j = 0; j < 3; j++) pos[j] = fg.B[j].position;
                var b2 = fg.B[2];
                pos[3] = b2.childCount > 0 && (b2.GetChild(0).position - pos[2]).sqrMagnitude > 1e-8f ? b2.GetChild(0).position : pos[2] + (pos[2] - pos[1]) * 0.8f;
                var acc = Quaternion.identity;      // the correction applied so far down the chain (world)
                var q = new Quaternion[3]; var p = new Vector3[4];
                var at = pos[0];
                var prevDir = f == 0 ? Vector3.zero : palmFwd;
                bool ok = true;
                for (int j = 0; j < 3 && ok; j++)
                {
                    var qj = acc * fg.B[j].rotation;
                    var seg = acc * (pos[j + 1] - pos[j]);
                    if (prevDir.sqrMagnitude > 0f)
                    {
                        var ax = qj * fg.AxisL[j];
                        var a1 = Vector3.ProjectOnPlane(prevDir, ax); var a2 = Vector3.ProjectOnPlane(seg, ax);
                        if (a1.sqrMagnitude > 1e-10f && a2.sqrMagnitude > 1e-10f)
                        {
                            float ang = Vector3.SignedAngle(a1, a2, ax);   // + = curled toward the palm (the curl axis' convention)
                            var fix = Quaternion.AngleAxis(-ang, ax);
                            acc = fix * acc; qj = fix * qj; seg = fix * seg;
                        }
                    }
                    q[j] = qj; p[j] = at;
                    at += seg; prevDir = seg;
                    ok = Finite(qj) && Finite(at);
                }
                if (!ok) continue;
                p[3] = at;
                var np = new Vector3[4]; var nq = new Quaternion[3];
                for (int j = 0; j < 4; j++) np[j] = wi * (p[j] - wp);
                for (int j = 0; j < 3; j++) nq[j] = wi * q[j];
                var rel = new Quaternion[3];
                rel[0] = nq[0];
                rel[1] = Quaternion.Inverse(nq[0]) * nq[1];
                rel[2] = Quaternion.Inverse(nq[1]) * nq[2];
                fg.NP = np; fg.NQ = nq; fg.Neutral = rel;
                fg.Fan = new FingerFan();
                fg.Fan.Build(3, np, nq, fg.AxisL, f == 0 ? ThumbMax : FingerMax, FanR);
                a.LiveNeutralN++;
            }
        }

        // The neutral hand from the bind pose (design 6.1): the bone-to-wrist matrix of each finger bone is Bwrist * Bj^-1 (Bj = the
        // mesh -> bone bind matrix), so its rotation part is the bone's neutral rotation in the wrist's frame and its translation the
        // joint's origin there (in the wrist's local units: times the wrist's lossy scale for metres). A renderer that carries the wrist
        // and a whole finger chain gives that finger its neutral pose; a body mesh is preferred. Anything missing leaves that finger's
        // Neutral null: it then rests in the animation's pose as before, and the clasp skips it.
        static void BuildNeutral(Arm a, Transform root)
        {
            a.NeutralN = 0; a.NeutralRend = ""; a.NeutralDiag = "";
            if (a.Fingers == null || root == null) return;
            SkinnedMeshRenderer best = null; int bestN = -1; bool bestBody = false;
            var bestIdx = new int[16];
            var idx = new int[16];
            foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r == null || r.sharedMesh == null || r.bones == null || VrHands.UnderSf(r.transform)) continue;
                var bones = r.bones;
                idx[0] = Array.IndexOf(bones, a.Wrist);
                if (idx[0] < 0) continue;
                int n = 0;
                for (int f = 0; f < 5; f++)
                {
                    var fg = a.Fingers[f];
                    bool ok = fg != null;
                    for (int j = 0; j < 3; j++)
                    {
                        int k = ok ? Array.IndexOf(bones, fg.B[j]) : -1;
                        idx[1 + f * 3 + j] = k;
                        if (k < 0) ok = false;
                    }
                    if (ok) n++;
                }
                bool body = r.name.ToLowerInvariant().Contains("body") || r.sharedMesh.name.ToLowerInvariant().Contains("body");
                if (n > bestN || (n == bestN && body && !bestBody))
                {
                    best = r; bestN = n; bestBody = body;
                    Array.Copy(idx, bestIdx, 16);
                }
            }
            if (best == null || bestN <= 0) return;
            var bp = best.sharedMesh.bindposes;
            var ls = a.Wrist.lossyScale;
            float sc = (Mathf.Abs(ls.x) + Mathf.Abs(ls.y) + Mathf.Abs(ls.z)) / 3f;
            if (!(sc > 1e-6f) || float.IsInfinity(sc) || bestIdx[0] >= bp.Length) return;
            var bw = bp[bestIdx[0]];
            var wi = Quaternion.Inverse(a.Wrist.rotation);
            float rootDev = 0f, axisDev = 0f;
            int got = 0;
            for (int f = 0; f < 5; f++)
            {
                var fg = a.Fingers[f];
                if (fg == null) continue;
                bool ok = true;
                var np = new Vector3[4]; var nq = new Quaternion[3];
                for (int j = 0; j < 3 && ok; j++)
                {
                    int k = bestIdx[1 + f * 3 + j];
                    if (k < 0 || k >= bp.Length) { ok = false; break; }
                    var m = bw * bp[k].inverse;
                    np[j] = (Vector3)m.GetColumn(3) * sc;
                    nq[j] = RotOf(m);
                    if (!Finite(np[j]) || np[j].sqrMagnitude > 0.25f || !Finite(nq[j])) ok = false;
                }
                if (!ok) continue;
                np[3] = np[2] + (np[2] - np[1]) * 0.8f;
                // the bind pose and the live rig must agree where the finger starts, else these are not the matrices for these bones
                float dev = (np[0] - wi * (fg.B[0].position - a.Wrist.position)).magnitude;
                if (dev > 0.02f) continue;
                rootDev = Mathf.Max(rootDev, dev);
                // the curl axis again, in the neutral pose: across the finger and the palm normal (the hand's own: it does not curl)
                var dn = np[1] - np[0];
                var axw = dn.sqrMagnitude > 1e-10f ? Vector3.Cross(dn.normalized, f == 0 ? ThumbToward(a.PalmNL, a.AcrossL) : a.PalmNL) : Vector3.zero;
                if (axw.sqrMagnitude > 1e-4f)
                {
                    axw.Normalize();
                    for (int j = 0; j < 3; j++)
                    {
                        var axl = Quaternion.Inverse(nq[j]) * axw;
                        axisDev = Mathf.Max(axisDev, Vector3.Angle(axl, fg.AxisL[j]));
                        fg.AxisL[j] = axl;
                    }
                }
                var rel = new Quaternion[3];
                rel[0] = nq[0];
                rel[1] = Quaternion.Inverse(nq[0]) * nq[1];
                rel[2] = Quaternion.Inverse(nq[1]) * nq[2];
                fg.NP = np; fg.NQ = nq; fg.Neutral = rel;
                fg.Fan = new FingerFan();
                fg.Fan.Build(3, np, nq, fg.AxisL, f == 0 ? ThumbMax : FingerMax, FanR);
                got++;
            }
            a.NeutralN = got;
            a.NeutralRend = best.name;
            a.NeutralDiag = "finger roots match the live rig to " + (rootDev * 1000f).ToString("0.0") + " mm, curl axes moved up to " + axisDev.ToString("0") + " deg from the animation pose's";
        }

        static bool Finite(Vector3 v) { return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)); }
        static bool Finite(Quaternion q) { return !(float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w)); }

        // The rotation of a bone-to-parent matrix from its z and y axes, so a scale in the matrix does not tilt it (PhysicsHands.Decompose).
        static Quaternion RotOf(Matrix4x4 m)
        {
            Vector3 c1 = m.GetColumn(1), c2 = m.GetColumn(2);
            return c1.sqrMagnitude > 1e-12f && c2.sqrMagnitude > 1e-12f ? Quaternion.LookRotation(c2.normalized, c1.normalized) : Quaternion.identity;
        }

        // Once per partner: where his fingers' neutral pose came from, or why they rest in the animation's.
        void LogNeutral(Rig r, GameObject go)
        {
            if (r.L.Fingers == null && r.R.Fingers == null) return;
            if (r.L.NeutralN + r.R.NeutralN > 0)
            {
                string rend = r.L.NeutralN > 0 ? r.L.NeutralRend : r.R.NeutralRend;
                Plugin.Log.LogInfo("Embody: neutral finger pose from the bind pose of '" + rend + "' (left " + r.L.NeutralN + "/5, right " + r.R.NeutralN + "/5 fingers); rest curl "
                    + (_cur != null ? _cur.RestCurl : 0.25f).ToString("0.00") + "; left: " + r.L.NeutralDiag + "; right: " + r.R.NeutralDiag);
            }
            if (r.L.LiveNeutralN + r.R.LiveNeutralN > 0)
                Plugin.Log.LogInfo("Embody: neutral finger pose straightened from the animation's hand (no bind pose for: left "
                    + r.L.LiveNeutralN + "/5, right " + r.R.LiveNeutralN + "/5 fingers)");
            else if (r.L.NeutralN + r.R.NeutralN == 0) Plugin.Log.LogInfo("Embody: no skinned renderer carries his finger bones: fingers rest in the animation's pose (no clasp stop either)");
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
            eye = EyeWorld();   // 0.11.7: plus the view offset (ViewForward / Up / Right)

            // The target the headset is put on: his eye smoothed once per frame (snapped on a new start, a snap request or a far jump).
            bool snap = !_eyeInit || _snapPending || (eye - _eyeSmooth).sqrMagnitude > 2.25f;
            _eyeSmooth = snap ? eye : Vector3.Lerp(_eyeSmooth, eye, 1f - Mathf.Exp(-dt / FollowSeconds));
            _eyeInit = true;
        }

        Vector3 EyeWorld()
        {
            var h = _rig.Head;
            var p = h.position + h.rotation * _eyeRel;
            if (_viewOff.sqrMagnitude > 1e-8f)
            {
                // his head's forward / up (TransformDirection ignores the hidden head's 0.001 scale)
                var f = h.TransformDirection(_rig.FwdL); var u = h.TransformDirection(_rig.UpL);
                var r = Vector3.Cross(u, f);
                if (r.sqrMagnitude > 1e-8f) p += r.normalized * _viewOff.x + u.normalized * _viewOff.y + f.normalized * _viewOff.z;
            }
            return p;
        }
        Vector3 _viewOff, _snapOff;

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

        Vector3 _snapCorr; int _snapAlign = -1;
        static float Clean(float v) { return float.IsNaN(v) || float.IsInfinity(v) ? 0f : Mathf.Clamp(v, -90f, 90f); }

        void ViewStep(EmbodySettings s, float dt)
        {
            // a changed alignment or correction re-snaps (the sliders act at once)
            if (_viewRig != null && (s.ViewAlign != _snapAlign || Mathf.Abs(Clean(s.ViewYaw) - _snapCorr.x) > 0.5f || Mathf.Abs(Clean(s.ViewPitch) - _snapCorr.y) > 0.5f
                                     || Mathf.Abs(Clean(s.ViewRoll) - _snapCorr.z) > 0.5f || (s.ViewOffset - _snapOff).sqrMagnitude > 1e-6f)) _snapPending = true;
            _viewOff = Finite(s.ViewOffset) ? Vector3.ClampMagnitude(s.ViewOffset, 0.3f) : Vector3.zero;
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
                // a pitch/roll correction from an earlier snap is taken out first (the rig back upright), so corrections never add up;
                // only when we tilted it (a game's own tilt is left alone)
                if ((_snapCorr.y != 0f || _snapCorr.z != 0f) && Vector3.Angle(rigRoot.up, Vector3.up) < 50f)
                    Turn(Quaternion.FromToRotation(rigRoot.up, Vector3.up), cam.transform.position);
                var rup = rigRoot.up;
                var hf = Facing(cam.transform.forward, cam.transform.up, rup);
                var tf = Facing(_hFwd, _hUp, rup);
                // 0.8.0: his chest's facing (shoulder line x up, signed by his head) when asked: his head is often turned in the
                // animation, and turning the view to it put his arms off to one side of yours
                string to = "his head's yaw";
                if (s.ViewAlign == 1 && _rig.L != null && _rig.R != null && _rig.L.Shoulder != null && _rig.R.Shoulder != null)
                {
                    var left = Vector3.ProjectOnPlane(_rig.L.Shoulder.position - _rig.R.Shoulder.position, rup);
                    var cf = Vector3.Cross(rup, left);
                    if (cf.sqrMagnitude > 1e-6f && left.sqrMagnitude > 1e-4f)
                    {
                        if (tf.sqrMagnitude > 1e-6f && Vector3.Dot(cf, tf) < 0f) cf = -cf;
                        tf = cf; to = "his chest's yaw";
                    }
                }
                float yaw = 0f;
                if (hf.sqrMagnitude > 1e-6f && tf.sqrMagnitude > 1e-6f)
                {
                    yaw = Vector3.SignedAngle(hf, tf, rup);
                    // About the headset, so it stays where it is while the world turns around it.
                    Turn(Quaternion.AngleAxis(yaw, rup), cam.transform.position);
                }
                // the user's corrections, about the headset too
                float cy = Clean(s.ViewYaw), cp = Clean(s.ViewPitch), cr = Clean(s.ViewRoll);
                if (cy != 0f) Turn(Quaternion.AngleAxis(cy, rup), cam.transform.position);
                if (cp != 0f || cr != 0f)
                {
                    var cfw = Vector3.ProjectOnPlane(cam.transform.forward, rup);
                    if (cfw.sqrMagnitude < 1e-6f) cfw = Vector3.ProjectOnPlane(_hFwd, rup);
                    if (cfw.sqrMagnitude > 1e-6f)
                    {
                        cfw.Normalize();
                        var right = Vector3.Cross(rup, cfw);
                        Turn(Quaternion.AngleAxis(-cp, right) * Quaternion.AngleAxis(cr, cfw), cam.transform.position);
                    }
                }
                _snapCorr = new Vector3(cy, cp, cr); _snapAlign = s.ViewAlign; _snapOff = s.ViewOffset;
                _eyeSmooth = EyeWorld(); _eyeInit = true;   // a new start: no smoothing from the old position
                Translate();
                if (!_snapLogged)
                {
                    _snapLogged = true;
                    Plugin.Log.LogInfo("Embody: snapped the view: turned " + yaw.ToString("0") + " deg to " + to + (cy != 0f || cp != 0f || cr != 0f ? " (+ correction yaw " + cy.ToString("0") + ", pitch " + cp.ToString("0") + ", roll " + cr.ToString("0") + " deg)" : "") + " and put the headset on his eye point (" + _eyeFrom + "), head axes from " + _rig.AxesFrom);
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
            LogStats();
        }

        // Every 10 s, only when something happened: how much each elbow swivelled, how often a clasp stopped on her.
        void LogStats()
        {
            float now = Time.unscaledTime;
            if (now < _nextStat) return;
            _nextStat = now + 10f;
            var l = _rig.L; var r = _rig.R;
            if (l.SwN + r.SwN + l.Stops + r.Stops + l.StopN + r.StopN > 0)
            {
                var sb = new StringBuilder("Embody: last 10 s: elbow swivel L ");
                SwivelWord(sb, l); sb.Append(", R "); SwivelWord(sb, r);
                sb.Append("; clasp stops L ").Append(l.Stops).Append(" R ").Append(r.Stops);
                sb.Append("; hand stop L "); StopWord(sb, l); sb.Append(", R "); StopWord(sb, r);
                Plugin.Log.LogInfo(sb.ToString());
            }
            l.StopN = r.StopN = 0; l.StopSum = r.StopSum = l.StopMax = r.StopMax = 0f; l.StopSeg = r.StopSeg = "";
            l.SwN = r.SwN = l.Stops = r.Stops = 0; l.SwSum = r.SwSum = l.SwMax = r.SwMax = 0f;
        }

        static void StopWord(StringBuilder sb, Arm a)
        {
            if (a.StopN == 0) sb.Append("none");
            else sb.Append("avg ").Append((a.StopSum / a.StopN * 100f).ToString("0.0")).Append(" cm max ").Append((a.StopMax * 100f).ToString("0.0"))
                .Append(" cm (deepest: ").Append(a.StopSeg.Length > 0 ? a.StopSeg : "?").Append(')');
        }

        static void SwivelWord(StringBuilder sb, Arm a)
        {
            if (a.SwN == 0) sb.Append("none");
            else sb.Append("avg ").Append((a.SwSum / a.SwN).ToString("0")).Append(" deg max ").Append(a.SwMax.ToString("0"));
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
            // The physics wrist is ctrl.position + ctrl.rotation * offset + dp (PhysicsHands.Target + Off): the displacement dq turns the
            // HAND, not the wrist offset (0.8.0: composing it into the offset put the hand 3.5 cm off for 10 cm and 20 deg).
            var raw = ctrl.position + dp + ctrl.rotation * offPos;
            // The target trails the controller a little (critically damped, 6 Hz): wrist and elbow lag on fast moves. Advanced once per
            // frame (save); the pre-render pass keeps the same trail as an offset on the new raw target.
            var T = save ? FollowStep(a, raw) : raw + a.Lag;
            var tq = dq * ctrl.rotation * Quaternion.Euler(offRot) * a.Kinv;
            // 0.8.0 (design 13.3a): without the physics hands his hand stops at her skin. The shift is measured at the RAW target (open
            // loop: what the stopped hand does cannot change it), smoothed, and added to the target the IK reaches; the yield ghost
            // stays at the raw controller target, so pushing deeper moves her limb and the stopped hand follows its surface.
            if (save) StopStep(a, T, tq);
            T += a.Stop;

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
            // 0.8.0 elbow swivel (design 6.4). The pole above comes from where the elbow is now: the animation's, unless nothing rewrote
            // the arm since our last write, then it already carries the swivel we applied (take that out again, so it is never added twice;
            // the pre-render pass finds our own elbow the same way).
            if (a.Written && Mathf.Abs(a.Applied) > 0.01f && (B - a.PlacedElbow).sqrMagnitude < 0.005f * 0.005f)
            {
                var un = Ik.SwivelPole(pole, dir, -a.Applied);
                if (un.sqrMagnitude > 1e-8f && Finite(un)) pole = un.normalized;
            }
            float cA = l1 * cosA, sA = l1 * sinA;
            if (save) SwivelStep(a, A, dir, pole, cA, sA, Tc);
            if (float.IsNaN(a.Swivel) || float.IsInfinity(a.Swivel)) a.Swivel = a.SwivelT = 0f;
            if (Mathf.Abs(a.Swivel) > 0.01f)
            {
                var sp = Ik.SwivelPole(pole, dir, a.Swivel);
                if (sp.sqrMagnitude > 1e-8f && Finite(sp)) pole = sp.normalized;
            }
            a.Applied = a.Swivel;
            var B2 = A + dir * cA + pole * sA;
            a.PlacedElbow = B2;

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

        // Penetration of his two arm capsules into her skin for a swivel phi (degrees): the upper arm shoulder -> elbow, and the forearm
        // from the elbow toward the wrist but not all the way (the hand itself is allowed to touch her; the wrist end would otherwise
        // read as the arm passing through her whenever the palm rests on her skin and no swivel could ever fix it).
        static float ArmPen(Vector3 A, Vector3 dir, Vector3 pole0, float cA, float sA, Vector3 Tc, float phi, out int seg)
        {
            var p = pole0;
            if (Mathf.Abs(phi) > 0.01f)
            {
                var sp = Ik.SwivelPole(pole0, dir, phi);
                if (sp.sqrMagnitude > 1e-8f && Finite(sp)) p = sp.normalized;
            }
            var B2 = A + dir * cA + p * sA;
            var Wm = Vector3.Lerp(B2, Tc, ForearmShare);
            int s1, s2; Vector3 n;
            float p1 = HerBody.CapsuleDepth(A, B2, UpperArmR, 0u, out s1, out n);
            float p2 = HerBody.CapsuleDepth(B2, Wm, ForearmR, 0u, out s2, out n);
            seg = p2 > p1 ? s2 : s1;
            return Mathf.Max(p1, p2);
        }

        // The elbow swivel target and its ease (design 6.4), once per frame. With the animation's pole (swivel 0) clear of her nothing
        // happens and the swivel eases back to 0. Otherwise the current target is kept while it is clear enough (hysteresis: it does not
        // flip between two good answers) and a search for the smallest swivel that clears her runs only when it is not, at most every
        // 0.25 s when it finds nothing better.
        void SwivelStep(Arm a, Vector3 A, Vector3 dir, Vector3 pole0, float cA, float sA, Vector3 Tc)
        {
            float target = 0f;
            if (_cur != null && _cur.ElbowAvoid && HerBody.Built)
            {
                int seg0;
                float pen0 = ArmPen(A, dir, pole0, cA, sA, Tc, 0f, out seg0);
                if (pen0 > SwivelStart)
                {
                    target = a.SwivelT;
                    int sg;
                    float penCur = Mathf.Abs(target) < 0.5f ? pen0 : ArmPen(A, dir, pole0, cA, sA, Tc, target, out sg);
                    float now = Time.unscaledTime;
                    if (penCur >= SwivelKeep && now >= a.NextSearch)
                    {
                        float bestPhi = 0f, bestPen = pen0;
                        bool clear = false;
                        for (int i = 1; i < SwivelDeg.Length && !clear; i++)
                        {
                            for (int sgn = 1; sgn >= -1; sgn -= 2)
                            {
                                float phi = SwivelDeg[i] * sgn;
                                float pen = ArmPen(A, dir, pole0, cA, sA, Tc, phi, out sg);
                                if (pen < bestPen) { bestPen = pen; bestPhi = phi; }
                                if (pen < SwivelOk) clear = true;   // the other sign of this size still competes for the smaller penetration
                            }
                        }
                        if (bestPen < penCur - 0.004f) target = bestPhi;
                        else a.NextSearch = now + SwivelRetry;
                        if (Mathf.Abs(target) > 0.5f && !a.SwivelLogged)
                        {
                            a.SwivelLogged = true;
                            string sn = seg0 >= 0 && seg0 < HerBody.SegNames.Length ? HerBody.SegNames[seg0] : "body";
                            Plugin.Log.LogInfo("Embody: his " + a.Side + " elbow swivels around her (first time: " + target.ToString("0") + " deg, the arm was "
                                + (pen0 * 100f).ToString("0.0") + " cm inside her " + sn + ")");
                        }
                    }
                }
            }
            a.SwivelT = target;
            float dt = Mathf.Max(_dt, 1e-4f);
            float d = a.SwivelT - a.Swivel;
            a.Swivel = Mathf.Abs(d) < 0.05f ? a.SwivelT : a.Swivel + d * (1f - Mathf.Exp(-SwivelEase * dt));
            float aw = Mathf.Abs(a.Swivel);
            if (aw > 1f) { a.SwN++; a.SwSum += aw; if (aw > a.SwMax) a.SwMax = aw; }
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

        static float Grip01(float v) { return float.IsNaN(v) ? 0f : Mathf.Clamp01(v); }

        // The finger commands (the physics hands' numbers): every finger rests at RestCurlF scaled by RestCurl / 0.25 and closes with the
        // grab button (Grip.GrabLeft/Right).
        void Commands(bool left, float[] cmd)
        {
            float rs = _cur != null ? Mathf.Clamp01(_cur.RestCurl) / 0.25f : 1f;
            float grab = Grip01(left ? Grip.GrabLeft : Grip.GrabRight);   // 0.8.0 (user): one grab button closes the whole hand, no pointing pose
            for (int f = 0; f < 5; f++)
            {
                float rest = Mathf.Clamp01(RestCurlF[f] * rs);
                cmd[f] = Mathf.Lerp(rest, 1f, grab);
            }
        }

        static readonly float[] _cmd = new float[5], _contact = new float[5];
        static readonly int[] _cseg = new int[5];

        // One step of the neutral-pose fingers' curl (design 6.2): the commands, the analytic fan against her skin (open-loop: it depends only
        // on the wrist's pose, so the fingers closing cannot change what it senses), DynamicLimbs' followers when some finger has touched,
        // the stop at contact + ClaspGive * (0.5 + Softness), then the ease (exponential 15/s, at most 8/s: DL's 0.15 s).
        void UpdateFingerCurl(Arm a, bool left, float dt)
        {
            Commands(left, _cmd);
            bool fan = HerBody.Built;   // the fan tests her skin shapes: without them the clasp has nothing to stop on
            float sumC = 0f; int nTouch = 0;
            for (int f = 0; f < 5; f++)
            {
                _contact[f] = 1f; _cseg[f] = -1;
                var fg = a.Fingers[f];
                if (!fan || fg == null || fg.Fan == null) continue;
                int sg;
                float c = fg.Fan.ConformAnalytic(a.Wrist.position, a.Wrist.rotation, 1f, out sg);
                if (float.IsNaN(c)) c = 1f;
                c = Mathf.Clamp01(c);
                _contact[f] = c; _cseg[f] = sg;
                if (c < 1f && f != 0) { sumC += c; nTouch++; }
            }
            float avg = nTouch > 0 ? sumC / nTouch : 1f;
            float soft = _cur != null ? Mathf.Clamp01(_cur.Softness) : 0.5f;
            bool stopped = false;
            for (int f = 0; f < 5; f++)
            {
                var fg = a.Fingers[f];
                if (fg == null) continue;
                float target = _cmd[f];
                if (nTouch > 0 && _contact[f] >= 1f && f != 0 && fg.Fan != null) target = Mathf.Max(target, FollowerK[f] * avg);
                float fin = target;
                if (_contact[f] < 1f)
                {
                    float give = HerBody.ClaspGive(_cseg[f]) * (0.5f + soft);
                    if (float.IsNaN(give) || give < 0f) give = 0f;
                    fin = Mathf.Min(target, _contact[f] + give);
                    if (fin < target - 0.02f)
                    {
                        stopped = true;
                        string sn = _cseg[f] >= 0 && _cseg[f] < HerBody.SegNames.Length ? HerBody.SegNames[_cseg[f]] : "body";
                        if (_claspLogged.Add(a.Side + sn))
                            Plugin.Log.LogInfo("Embody: " + a.Side + " clasp stopped on her " + sn + " at curl " + fin.ToString("0.00") + " (cmd " + target.ToString("0.00") + ", give " + give.ToString("0.00") + ")");
                    }
                }
                fin = Mathf.Clamp01(fin);
                if (!a.FInit) a.FCurl[f] = fin;
                else
                {
                    float step = (fin - a.FCurl[f]) * (1f - Mathf.Exp(-EaseRate * dt));
                    a.FCurl[f] += Mathf.Clamp(step, -EaseMax * dt, EaseMax * dt);
                }
            }
            a.FInit = true;
            if (stopped && !a.WasStopped) a.Stops++;
            a.WasStopped = stopped;
        }

        // Curls his fingers on top of a base pose: the simulated fingers of the physics hand when it has them (its curl angle per bone,
        // applied about his own curl axis), else the commands (every finger from the grab button, stopping on her skin) or, with
        // NeutralFingers off, the grab button alone as in 0.7.x. The base is the neutral hand from the bind pose where we have it (world
        // rotations, top-down per finger so helper bones between do not matter), else the animation's own pose.
        // dt 0: no step (the pre-render pass writes the same pose again).
        void WriteFingers(Arm a, float dt)
        {
            bool left = a.Side == "left";
            float[] curl = left ? PhysicsHands.LeftFingerCurlDeg : PhysicsHands.RightFingerCurlDeg;
            bool useSim = PhysicsHands.Active && (left ? PhysicsHands.LeftFingersValid : PhysicsHands.RightFingersValid)
                          && curl != null && curl.Length >= 15;
            bool cmdMode = _cur != null && _cur.NeutralFingers && !useSim;
            bool neutral = _cur != null && _cur.NeutralFingers;
            if (cmdMode)
            {
                if (dt > 0f || !a.FInit) UpdateFingerCurl(a, left, Mathf.Max(dt, 1e-4f));
            }
            else
            {
                a.FInit = false; a.WasStopped = false;
                float grip = Grip01(left ? Grip.GrabLeft : Grip.GrabRight);
                if (a.Curl < 0f) a.Curl = grip;
                else if (dt > 0f) a.Curl += (grip - a.Curl) * (1f - Mathf.Exp(-CurlRate * dt));
            }

            string src = (useSim ? "simulated" : cmdMode ? "grab button" : "grab button alone") + (neutral ? ", neutral base" : "");
            if (src != a.FingerSrc)
            {
                a.FingerSrc = src;
                Plugin.Log.LogInfo("Embody: his " + a.Side + " fingers now follow " + (useSim ? "the physics hand's simulated fingers"
                    : cmdMode ? "the grab button (Grip." + (left ? "Left" : "Right") + ", " + Grip.Source + ") with the clasp stopping on her"
                    : "the grab button (Grip." + (left ? "Left" : "Right") + ", " + Grip.Source + ")")
                    + (neutral ? " on the neutral hand from the bind pose" : " on the animation's pose")
                    + (PhysicsHands.Active && !useSim ? " [its fingers are not valid]" : ""));
            }

            var wq = a.Wrist.rotation;
            for (int f = 0; f < 5; f++)
            {
                var fg = a.Fingers[f];
                if (fg == null) continue;
                bool nb = neutral && fg.Neutral != null && Finite(wq);
                float cf = cmdMode ? a.FCurl[f] : a.Curl;
                for (int j = 0; j < 3; j++)
                {
                    var b = fg.B[j];
                    if (b == null) continue;
                    float ang = (f == 0 ? ThumbMax[j] : FingerMax[j]) * cf;
                    if (useSim)
                    {
                        float sa = curl[f * 3 + j];   // signed twist about the curl axis: a sideways swing or a backward bend is not a curl
                        // 0.11.7: per joint limits (the thumb went to 120 deg): -10 deg back, 1.25 x the full-grip curl forward
                        if (!float.IsNaN(sa) && !float.IsInfinity(sa)) ang = Mathf.Clamp(sa, f == 0 ? -5f : -10f, 1.25f * (f == 0 ? ThumbMax[j] : FingerMax[j]));
                    }
                    if (float.IsNaN(ang) || float.IsInfinity(ang)) ang = 0f;
                    var cur = b.localRotation;
                    // Our last write still there: the animation did not write this bone, so its pose is the one from before our write.
                    var baseQ = fg.Written[j] && Quaternion.Angle(cur, fg.Post[j]) < 0.05f ? fg.Pre[j] : cur;
                    fg.Pre[j] = baseQ;
                    if (nb)
                    {
                        var parentRot = j == 0 ? wq : fg.B[j - 1].rotation;   // the bone above, already curled this pass
                        b.rotation = parentRot * fg.Neutral[j];
                        baseQ = b.localRotation;
                    }
                    var q = baseQ * Quaternion.AngleAxis(ang, fg.AxisL[j]);
                    b.localRotation = q;
                    fg.Post[j] = q;
                    fg.Written[j] = true;
                }
            }
        }

        // ---- 0.8.0 ghost capsules: his hands at the controller, for what HerBody lets push her ----

        int Ghost(WorldCap[] into, int start, int max)
        {
            if (PhysicsHands.Active || !_active || _failed || _cur == null || _rig == null || _rig.Go == null) return 0;
            int n = GhostArm(_rig.L, _cur.Hands == 1 || _cur.Hands == 3, VrHands.LeftController, _cur.LeftPos, _cur.LeftRot, 0, into, start, max);
            n += GhostArm(_rig.R, _cur.Hands == 2 || _cur.Hands == 3, VrHands.RightController, _cur.RightPos, _cur.RightRot, 1, into, start + n, max - n);
            return n;
        }

        static readonly Vector3[] _fkP = new Vector3[4];

        // The capsules of one hand at the pose Place would give it (the controller + its offsets + last frame's trail; no physics offset
        // since the physics hands are off): the palm (wrist -> middle root, and two more toward the index and pinky roots) and, where the
        // neutral hand is known, the three bone segments of each finger at the current curl.
        int GhostArm(Arm a, bool want, Transform ctrl, Vector3 offPos, Vector3 offRot, int hand, WorldCap[] into, int at, int room)
        {
            if (room <= 0 || !want || !a.Ready || ctrl == null || a.Fingers == null || !a.Written || a.PlacedFrame < Time.frameCount - 1) return 0;
            var cq = ctrl.rotation;
            var T = ctrl.position + cq * offPos + a.Lag;
            var tq = cq * Quaternion.Euler(offRot) * a.Kinv;
            if (!Finite(T) || !Finite(tq)) return 0;
            return BuildHand(a, T, tq, hand, into, at, room);
        }

        // The hand's capsules at the pose (T, tq) for the given hand: the palm (wrist -> middle root, and two more toward the index and
        // pinky roots) and, where the neutral hand is known, the three bone segments of each finger at the current curl. The ghost and
        // the stop at her skin use the same builder (the stop tests the candidate pose, the ghost publishes the raw controller pose).
        int BuildHand(Arm a, Vector3 T, Quaternion tq, int hand, WorldCap[] into, int at, int room)
        {
            if (a.GhostBy == null || a.GhostIdx != _partnerIdx)
            {
                a.GhostIdx = _partnerIdx;
                a.GhostBy = "your " + a.Side + " hand (" + (_partnerIdx == 1 ? "BadGuy" : "Player") + ")";
            }
            int n = 0;
            int mid = a.Fingers[2] != null ? 2 : a.Fingers[1] != null ? 1 : a.Fingers[3] != null ? 3 : a.Fingers[4] != null ? 4 : -1;
            if (mid >= 0)
            {
                var pm = a.RootW[mid];
                n += Put(into, at + n, room - n, T, T + tq * pm, GhostPalmR, hand, a.GhostBy);
                if (mid != 1 && a.Fingers[1] != null) n += Put(into, at + n, room - n, T, T + tq * Vector3.Lerp(pm, a.RootW[1], 0.4f), GhostPalmR, hand, a.GhostBy);
                if (mid != 4 && a.Fingers[4] != null) n += Put(into, at + n, room - n, T, T + tq * Vector3.Lerp(pm, a.RootW[4], 0.4f), GhostPalmR, hand, a.GhostBy);
            }
            bool cmdMode = _cur.NeutralFingers && a.FInit;
            for (int f = 0; f < 5; f++)
            {
                var fg = a.Fingers[f];
                if (fg == null || fg.Neutral == null || !_cur.NeutralFingers) continue;
                float c = cmdMode ? a.FCurl[f] : Mathf.Max(a.Curl, 0f);
                FingerFk(fg, f, Mathf.Clamp01(c), _fkP);
                float r = f == 0 ? GhostThumbR : GhostFingerR;
                for (int j = 0; j < 3; j++) n += Put(into, at + n, room - n, T + tq * _fkP[j], T + tq * _fkP[j + 1], r, hand, a.GhostBy);
            }
            return n;
        }

        // ---- 0.8.0 hand stop at her skin (design 13.3a) ----

        static readonly WorldCap[] _dcap = new WorldCap[48];
        const float StopHz = 12f, StopMaxShift = 0.25f, StopMinPen = 0.001f;

        // How far a hand may sink into her skin before it stops, where the soft zones / GPU contact dent it.
        float SkinAllowance(int seg) { return HerBody.SkinAllowance(seg); }   // the part's tissue depth by softness (HerBody)

        // The shift (metres, world) that takes his hand out of her skin at the candidate pose: three passes along the deepest penetration.
        Vector3 StopShift(Arm a, Vector3 T, Quaternion tq, out int deepSeg)
        {
            deepSeg = -1;
            int n = BuildHand(a, T, tq, a.Side == "left" ? 0 : 1, _dcap, 0, _dcap.Length);
            var shift = Vector3.zero;
            for (int it = 0; it < 3; it++)
            {
                float deepest = 0f; var nrm = Vector3.zero; int dseg = -1;
                for (int i = 0; i < n; i++)
                {
                    var c = _dcap[i];
                    int seg; Vector3 nr;
                    var A = c.A + shift; var B = c.B + shift;
                    float pen = HerBody.CapsuleDepth(A, B, c.R, 0u, out seg, out nr);
                    if (!(pen > 0f)) continue;
                    float al = SkinAllowance(seg);
                    if (pen <= al) continue;     // inside what the skin gives
                    pen = HerBody.CapsuleDepth(A, B, Mathf.Max(c.R - al, 0.001f), 0u, out seg, out nr);
                    if (pen > deepest) { deepest = pen; nrm = nr; dseg = seg; }
                }
                if (deepest <= StopMinPen) break;
                if (it == 0) deepSeg = dseg;
                shift -= nrm * deepest;
            }
            if (!Finite(shift)) return Vector3.zero;
            float m = shift.magnitude;
            return m > StopMaxShift ? shift * (StopMaxShift / m) : shift;
        }

        // Once per frame (not in the pre-render pass, which reuses a.Stop): the target shift, then the exact critically damped step toward it
        // (12 Hz, the FollowStep form). Nothing tests while the physics hands run (their hull is the contact) or her body isn't built.
        void StopStep(Arm a, Vector3 T, Quaternion tq)
        {
            var shift = Vector3.zero;
            int seg = -1;
            if (!PhysicsHands.Active && HerBody.Built && a.Fingers != null && Finite(T) && Finite(tq))
                shift = StopShift(a, T, tq, out seg);
            float dt = Mathf.Max(_dt, 1e-4f);
            if (!a.StopInit || _dtRaw > 0.25f) { a.Stop = shift; a.StopV = Vector3.zero; a.StopInit = true; }
            else
            {
                float w = 2f * Mathf.PI * StopHz;
                var e = a.Stop - shift;
                var c2 = a.StopV + w * e;
                float ex = Mathf.Exp(-w * dt);
                a.Stop = shift + (e + c2 * dt) * ex;
                a.StopV = (a.StopV - w * c2 * dt) * ex;
                if (shift.sqrMagnitude < 1e-12f && a.Stop.sqrMagnitude < 1e-8f && a.StopV.sqrMagnitude < 1e-6f) { a.Stop = Vector3.zero; a.StopV = Vector3.zero; }
            }
            if (!Finite(a.Stop)) { a.Stop = Vector3.zero; a.StopV = Vector3.zero; }
            float m = a.Stop.magnitude;
            if (m > 0.003f)
            {
                a.StopN++; a.StopSum += m;
                if (m > a.StopMax) { a.StopMax = m; if (seg >= 0 && seg < HerBody.SegNames.Length) a.StopSeg = HerBody.SegNames[seg]; }
                if (!a.StopLogged)
                {
                    a.StopLogged = true;
                    Plugin.Log.LogInfo("Embody: his " + a.Side + " hand stops at her skin (analytic; allowance soft 1.0-3.0 cm, firm 0.5 cm)");
                }
            }
        }

        static int Put(WorldCap[] into, int i, int room, Vector3 A, Vector3 B, float r, int hand, string by)
        {
            if (room <= 0 || i < 0 || i >= into.Length) return 0;
            into[i] = new WorldCap { A = A, B = B, R = r, Hand = hand, By = by, Src = null };
            return 1;
        }

        // Forward kinematics of one finger from its neutral pose at curl fraction c, in the wrist's frame: the same composition WriteFingers
        // makes with the bones (each bone's neutral rotation relative to the one above, then its curl about its own axis).
        static void FingerFk(Finger fg, int f, float c, Vector3[] outP)
        {
            var mx = f == 0 ? ThumbMax : FingerMax;
            var q = fg.NQ[0] * Quaternion.AngleAxis(mx[0] * c, fg.AxisL[0]);
            var p = fg.NP[0];
            outP[0] = p;
            for (int j = 1; j < 3; j++)
            {
                var qPrevN = Quaternion.Inverse(fg.NQ[j - 1]);
                p = p + q * (qPrevN * (fg.NP[j] - fg.NP[j - 1]));
                q = q * (qPrevN * fg.NQ[j]) * Quaternion.AngleAxis(mx[j] * c, fg.AxisL[j]);
                outP[j] = p;
            }
            outP[3] = p + q * (Quaternion.Inverse(fg.NQ[2]) * (fg.NP[3] - fg.NP[2]));
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
            a.FInit = false; a.WasStopped = false;
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
            a.Stop = a.StopV = Vector3.zero; a.StopInit = false;
            a.Swivel = a.SwivelT = a.Applied = 0f; a.NextSearch = 0f;   // the arm goes back to the animation's elbow with the arm itself
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
