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
