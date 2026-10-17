using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SfMorph
{
    // What the game itself knows about its woman (Assembly-CSharp, through
    // reflection so the mod doesn't depend on its exact build):
    // - there is one, "reika": MovePlayerAndGirl.reika;
    // - her bones have no names in code, but the game holds them: FinalIK's
    //   BipedIK references on FacialFunction (pelvis, spine, limbs, head) and the
    //   breast DynamicBone chains / CCD IK on LadyBreastPhysics_Function.
    // The game never scales bones or sets blendshapes itself (it swaps whole
    // meshes for face types and pregnancy), so nothing of ours is overwritten.
    internal static class GameRig
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static bool _typesLooked;
        static Type _move, _facial, _breast;

        static void Types()
        {
            if (_typesLooked) return;
            _typesLooked = true;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != "Assembly-CSharp") continue;
                _move = a.GetType("MovePlayerAndGirl");
                _facial = a.GetType("FacialFunction");
                _breast = a.GetType("LadyBreastPhysics_Function");
            }
            Plugin.Log.LogInfo("Game rig: " + (_move != null ? "MovePlayerAndGirl found" : "MovePlayerAndGirl missing (women found by name instead)")
                               + (_facial != null ? ", FacialFunction" : "") + (_breast != null ? ", LadyBreastPhysics_Function" : ""));
        }

        // The woman's root object, or null outside a scene with her.
        public static Transform Lady()
        {
            Types();
            if (_move == null) return null;
            var m = UnityEngine.Object.FindObjectOfType(_move);
            if (m == null) return null;
            var go = Get(m, "reika") as GameObject;
            return go != null ? go.transform : null;
        }

        // The other people in the scene (MovePlayerAndGirl.player, .badGuy), whose hands press into her (soft zones).
        public static List<GameObject> Partners()
        {
            Types();
            var l = new List<GameObject>();
            if (_move == null) return l;
            var m = UnityEngine.Object.FindObjectOfType(_move);
            if (m == null) return l;
            foreach (var f in new[] { "player", "badGuy" }) { var go = Get(m, f) as GameObject; if (go != null) l.Add(go); }
            return l;
        }

        // Body part (Region id) -> the game's own bones for it, only those under the lady.
        public static Dictionary<string, List<Transform>> Parts(Transform lady)
        {
            var d = new Dictionary<string, List<Transform>>();
            Types();
            if (lady == null) return d;
            if (_facial != null)
            {
                var refs = Get(Get(UnityEngine.Object.FindObjectOfType(_facial), "bipedIK"), "references");
                if (refs != null)
                {
                    Add(d, lady, "hips", refs, "pelvis");
                    Add(d, lady, "thighs", refs, "leftThigh", "rightThigh");
                    Add(d, lady, "calves", refs, "leftCalf", "rightCalf");
                    Add(d, lady, "feet", refs, "leftFoot", "rightFoot");
                    Add(d, lady, "upperarms", refs, "leftUpperArm", "rightUpperArm");
                    Add(d, lady, "forearms", refs, "leftForearm", "rightForearm");
                    Add(d, lady, "hands", refs, "leftHand", "rightHand");
                    Add(d, lady, "head", refs, "head");
                    // FinalIK's spine runs from above the pelvis to the bone the arms hang from (no neck):
                    // the first is the waist, the last the ribcage; the head's parent is the neck.
                    var spine = Get(refs, "spine") as Transform[];
                    if (spine != null && spine.Length >= 2) { Put(d, lady, "waist", spine[0]); Put(d, lady, "ribcage", spine[spine.Length - 1]); }
                    var head = Get(refs, "head") as Transform;
                    if (head != null && head.parent != null && (spine == null || Array.IndexOf(spine, head.parent) < 0))
                    {
                        // The top of the neck chain (Neck_M, not its helper NeckPart1_M under it).
                        var n = head.parent;
                        while (n.parent != null && Array.IndexOf(spine ?? new Transform[0], n.parent) < 0 && Body.Norm(n.parent.name).Contains("neck")) n = n.parent;
                        Put(d, lady, "neck", n);
                    }
                }
            }
            if (_breast != null)
            {
                var b = UnityEngine.Object.FindObjectOfType(_breast);
                // The breast physics chains start at the breast bones (DynamicBone never moves its root).
                foreach (var side in new[] { "left_breast_Physics", "right_breast_Physics" })
                {
                    var bones = Get(Get(b, side), "physicsBone") as IEnumerable;
                    if (bones == null) continue;
                    foreach (var db in bones) Put(d, lady, "breasts", Get(db, "m_Root") as Transform);
                }
                // Otherwise the first bone of the breast-grab IK chain.
                if (!d.ContainsKey("breasts"))
                    foreach (var side in new[] { "leftBreastCCDIK", "rightBreastCCDIK" })
                    {
                        var bones = Get(Get(Get(b, side), "solver"), "bones") as IList;
                        if (bones != null && bones.Count > 0) Put(d, lady, "breasts", Get(bones[0], "transform") as Transform);
                    }
            }
            return d;
        }

        static void Add(Dictionary<string, List<Transform>> d, Transform lady, string id, object refs, params string[] fields)
        {
            foreach (var f in fields) Put(d, lady, id, Get(refs, f) as Transform);
        }

        static void Put(Dictionary<string, List<Transform>> d, Transform lady, string id, Transform t)
        {
            if (t == null || !t.IsChildOf(lady)) return;
            List<Transform> l;
            if (!d.TryGetValue(id, out l)) { l = new List<Transform>(); d[id] = l; }
            if (!l.Contains(t)) l.Add(t);
        }

        // ---- 0.7.0 (Embody): the partner, the game's VR state and the VR camera ----
        // The game's own VR POV (PlayerController.vrPovActive) is unreliable: sometimes the camera is not at his
        // eyes but elsewhere. So Embody reads these only to find the camera rig and to explain what it sees in
        // the log; the view is aligned by us, never by asking the game.

        // A game object found once with FindObjectOfType, kept, and searched for again (at most every 2 s) only
        // when it is gone, so per-frame callers never search.
        sealed class Seen { public Type T; public UnityEngine.Object Obj; public float Next; }
        static readonly Seen _seenMove = new Seen(), _seenPlayer = new Seen(), _seenCamSys = new Seen(), _seenVrProc = new Seen();
        static bool _types2Looked;
        static Camera _vrCam;
        static float _nextVrCam;
        public static string VrCameraSource = "";

        static void Types2()
        {
            if (_types2Looked) return;
            _types2Looked = true;
            Types();
            _seenMove.T = _move;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != "Assembly-CSharp") continue;
                _seenPlayer.T = a.GetType("PlayerController");
                _seenCamSys.T = a.GetType("Camera_system");
                _seenVrProc.T = a.GetType("VR_GameProcess_System");
            }
            Plugin.Log.LogInfo("Game rig (VR): " + (_seenPlayer.T != null ? "PlayerController " : "") + (_seenCamSys.T != null ? "Camera_system " : "")
                               + (_seenVrProc.T != null ? "VR_GameProcess_System" : "") + (_seenPlayer.T == null && _seenCamSys.T == null && _seenVrProc.T == null ? "none of the VR types found" : ""));
        }

        // The scene's instance of a game type (null for a static class or when absent).
        static UnityEngine.Object Instance(Seen s)
        {
            if (s.T == null || !typeof(UnityEngine.Object).IsAssignableFrom(s.T)) return null;
            if (s.Obj != null) return s.Obj;
            if (Time.unscaledTime < s.Next) return null;
            s.Next = Time.unscaledTime + 2f;
            s.Obj = UnityEngine.Object.FindObjectOfType(s.T);
            return s.Obj;
        }

        // A field or property of a game object, static or instance (the VR_* classes may be either).
        internal static object Field(object holder, Type t, string name)
        {
            if (t == null) return null;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var f = t.GetField(name, all);
            if (f != null) return f.IsStatic ? f.GetValue(null) : (holder != null ? f.GetValue(holder) : null);
            var p = t.GetProperty(name, all);
            var m = p != null ? p.GetGetMethod(true) : null;
            if (m == null || m.GetParameters().Length != 0) return null;
            return m.IsStatic ? m.Invoke(null, null) : (holder != null ? m.Invoke(holder, null) : null);
        }

        internal static Transform AsTransform(object o)
        {
            var go = o as GameObject;
            if (go != null) return go.transform;
            var c = o as Component;
            return c != null ? c.transform : null;
        }

        internal static Camera AsCamera(object o)
        {
            var cam = o as Camera;
            if (cam != null) return cam;
            var go = o as GameObject;
            if (go != null) return go.GetComponentInChildren<Camera>(true);
            var c = o as Component;
            return c != null ? c.GetComponentInChildren<Camera>(true) : null;
        }

        // Cached MovePlayerAndGirl.player (0) / .badGuy (1); null when absent. Cheap enough to call every second.
        public static GameObject Partner(int which)
        {
            Types2();
            var m = Instance(_seenMove);
            return m == null ? null : Get(m, which == 1 ? "badGuy" : "player") as GameObject;
        }

        // The game's PlayerController (vrCameraType, vrPovActive, vr_PovCamera_Location, camera_vrCam, vr_Camera_Mov), or null.
        public static UnityEngine.Object PlayerController()
        {
            Types2();
            return Instance(_seenPlayer);
        }

        // VR_GameProcess_System.VR_Mode: the object holding the XR rig, hands and UI, or null.
        public static GameObject VrMode()
        {
            Types2();
            var t = AsTransform(Field(Instance(_seenVrProc), _seenVrProc.T, "VR_Mode"));
            return t != null ? t.gameObject : null;
        }

        // A named field of the PlayerController as a transform (vr_PovCamera_Location, vr_Camera_Mov, camera_vrCam).
        public static Transform PlayerTransform(string field)
        {
            var pc = PlayerController();
            return pc == null ? null : AsTransform(Field(pc, pc.GetType(), field));
        }

        // "vrCameraType='...', vrPovActive=..." for logs that explain who may be moving the camera.
        public static string PovInfo()
        {
            var pc = PlayerController();
            if (pc == null) return "PlayerController not found";
            var t = pc.GetType();
            return "vrCameraType='" + Field(pc, t, "vrCameraType") + "', vrPovActive=" + Field(pc, t, "vrPovActive");
        }

        // The camera that renders to the headset: a stereo camera the game names (PlayerController.camera_vrCam,
        // Camera_system.camera_VRCam), else any stereo camera, else the named ones, else the main camera.
        // Cached, searched again at most every 2 s; the source is in VrCameraSource.
        public static Camera VrCamera()
        {
            float now = Time.unscaledTime;
            if (_vrCam != null && now < _nextVrCam && _vrCam.isActiveAndEnabled) return _vrCam;
            _nextVrCam = now + 2f;
            Types2();
            var pc = Instance(_seenPlayer);
            var cs = Instance(_seenCamSys);
            var named = new[] { AsCamera(pc != null ? Field(pc, pc.GetType(), "camera_vrCam") : null),
                                AsCamera(Field(cs, _seenCamSys.T, "camera_VRCam")) };
            var src = new[] { "PlayerController.camera_vrCam", "Camera_system.camera_VRCam" };
            Camera best = null; string how = "";
            for (int i = 0; i < named.Length && best == null; i++)
                if (named[i] != null && named[i].isActiveAndEnabled && named[i].stereoEnabled) { best = named[i]; how = src[i] + " (stereo)"; }
            if (best == null)
                foreach (var c in Camera.allCameras)   // timing-lint: ok VrCamera re-searches at most every 2 s
                    if (c != null && c.stereoEnabled) { best = c; how = "the stereo camera"; break; }
            for (int i = 0; i < named.Length && best == null; i++)
                if (named[i] != null && named[i].isActiveAndEnabled) { best = named[i]; how = src[i] + " (not stereo: no headset?)"; }
            if (best == null) { best = Camera.main; how = "Camera.main"; }
            if (best != _vrCam)
                Plugin.Log.LogInfo("Embody: VR camera is " + (best != null ? "'" + PathOf(best.transform) + "'" : "missing") + " from " + how);
            _vrCam = best;
            VrCameraSource = how;
            return best;
        }

        // "Root/Child/Leaf" for logs.
        public static string PathOf(Transform t)
        {
            if (t == null) return "(none)";
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        static object Get(object o, string name)
        {
            if (o == null) return null;
            var uo = o as UnityEngine.Object;
            if (!ReferenceEquals(uo, null) && uo == null) return null; // destroyed
            var t = o.GetType();
            var f = t.GetField(name, Any);
            if (f != null) return f.GetValue(o);
            var p = t.GetProperty(name, Any);
            return p != null && p.GetIndexParameters().Length == 0 ? p.GetValue(o, null) : null;
        }
    }
}
