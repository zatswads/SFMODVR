using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace SfMorph
{
    // Settings of the physics hands (the integrator fills them from the config every frame).
    internal class HandsSettings
    {
        public bool On = false, Collide = true, HideVisual = false, ShowColliders = false;
        public int Mesh = 0;           // 0 the game's own hands (extracted from the player's body), 1 simple primitives
        public float Softness = 0.5f;  // 0..1, how deep her soft zones may yield (also how much her colliders are inset)
        // Controller -> wrist (m, degrees), in the controller's axes, shared with Embody: the position is where the wrist sits
        // relative to the tracked pose; the rotation is added on top of an automatic alignment (fingers along the controller's
        // forward, thumb along its up, palm toward the body's midline), so zero is the natural grip.
        public Vector3 LeftPos = new Vector3(-0.0325f, -0.0379f, -0.1012f), LeftRot = Vector3.zero;
        public Vector3 RightPos = new Vector3(0.0325f, -0.0379f, -0.1012f), RightRot = Vector3.zero;
    }

    // Collision events only reach scripts on the rigidbody's object, so this tiny component forwards them (log of first contacts).
    internal class PhysicsHandProbe : MonoBehaviour
    {
        public bool Left;
        void OnCollisionEnter(Collision c) { PhysicsHands.NoteContact(Left, c); }
    }

    // The user's VR hands as physics objects: a rigidbody palm with finger capsules that follows the controller by velocity,
    // collides with a kinematic capsule model of her body (private layers, nothing else in the scene feels them) and shows a
    // hand mesh that stays 1:1 with the controller in free air and shows the blocked displacement when pushing into her.
    // Her soft zones read the visual hand's capsules (Capsules). Nothing here runs per frame except cheap pose writes.
    internal static class PhysicsHands
    {
        public static bool Active;                                  // hands are built: the integrator uses Capsules instead of VrHands' "vr" ones
        public static int Version;                                  // bumped whenever Capsules changes
        public static string Status = "";
        public static Transform LeftWrist, RightWrist;              // the visual wrists (Embody's IK targets when it follows the hands)
        // Per hand, this frame: how far the physics body stands from the controller target (the displacement the visual pose gets).
        // Zero / identity when the hand is missing, kinematic (Collide off) or not built. Embody composes it into its wrist targets.
        public static Vector3 LeftDp, RightDp;
        public static Quaternion LeftDq = Quaternion.identity, RightDq = Quaternion.identity;
        public static readonly List<SoftCapsule> Capsules = new List<SoftCapsule>();

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const string Pfx = "SfPhys";
        const string HerPfx = "SfPhysHer_";

        // Sizes (metres, from the design).
        const float PalmR = 0.035f, FingerR = 0.011f, ThumbR = 0.0125f, FingerVisR = 0.0095f;
        static readonly float[] FingerMax = { 80f, 90f, 60f };     // curl at full grip, proximal / middle / distal
        static readonly float[] ThumbMax = { 20f, 35f, 45f };

        // ---- game types (Assembly-CSharp isn't referenced) ----

        static bool _typesLooked;
        static Type _handType, _moveType;

        static void Types()
        {
            if (_typesLooked) return;
            _typesLooked = true;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != "Assembly-CSharp") continue;
                _handType = _handType ?? a.GetType("VR_HandController");
                _moveType = _moveType ?? a.GetType("MovePlayerAndGirl");
            }
            Plugin.Log.LogInfo("PhysicsHands: " + (_handType != null ? "VR_HandController" : "no VR_HandController (controller models stay, grip from XR only)")
                               + (_moveType != null ? ", MovePlayerAndGirl" : ", no MovePlayerAndGirl (primitive hands)"));
        }

        static object Get(object o, string name)
        {
            if (o == null) return null;
            var uo = o as UnityEngine.Object;
            if (!ReferenceEquals(uo, null) && uo == null) return null;   // destroyed
            var t = o.GetType();
            var f = t.GetField(name, Any);
            if (f != null) return f.GetValue(o);
            var p = t.GetProperty(name, Any);
            return p != null && p.GetIndexParameters().Length == 0 ? p.GetValue(o, null) : null;
        }

        static bool IsUi(Transform t)
        {
            for (var p = t; p != null; p = p.parent) if (p is RectTransform) return true;   // a Canvas always sits on a RectTransform
            return false;
        }

        static bool Finite(Vector3 v) { return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)); }
        static bool Finite(Quaternion q) { return !(float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w) || float.IsInfinity(q.x) || float.IsInfinity(q.y) || float.IsInfinity(q.z) || float.IsInfinity(q.w)); }

        static readonly HashSet<string> _once = new HashSet<string>();
        static void LogOnce(string key, string msg) { if (_once.Add(key)) Plugin.Log.LogInfo("PhysicsHands: " + msg); }

        static void SetStatus(string s)
        {
            if (Status == s) return;
            Status = s;
            Plugin.Log.LogInfo("PhysicsHands: " + (s.Length == 0 ? "(status cleared)" : s));
        }

        // ---- the game objects we look up now and then (never per frame) ----

        static Component _moveObj, _hcObj;
        static float _moveNext, _hcNext;

        static GameObject FindPlayer()
        {
            Types();
            if (_moveType == null) return null;
            if (_moveObj == null)
            {
                if (Time.unscaledTime < _moveNext) return null;
                _moveNext = Time.unscaledTime + 2f;
                _moveObj = UnityEngine.Object.FindObjectOfType(_moveType) as Component;
                if (_moveObj == null) return null;
            }
            return Get(_moveObj, "player") as GameObject;
        }

        static Component HandController()
        {
            Types();
            if (_handType == null) return null;
            if (_hcObj == null)
            {
                if (Time.unscaledTime < _hcNext) return null;
                _hcNext = Time.unscaledTime + 2f;
                _hcObj = UnityEngine.Object.FindObjectOfType(_handType) as Component;
            }
            return _hcObj;
        }

        // ================= geometry specs =================

        sealed class BoneSpec
        {
            public string Name;
            public int Parent = -1;                       // index into Bones, -1: child of the hand root
            public Vector3 Pos, Scale = Vector3.one;      // local to the parent
            public Quaternion Rot = Quaternion.identity;
            public Matrix4x4 M = Matrix4x4.identity;      // hand-root space (the wrist's own space), root units
        }

        sealed class ShapeSpec                            // a collider or a primitive of the visual
        {
            public int Bone;
            public bool Box;
            public Vector3 A, B;                          // capsule ends in root units (box: both its centre)
            public Vector3 Size;                          // box size (metres): x across the palm, y thick, z along the fingers
            public Quaternion Rot = Quaternion.identity;  // box rotation in root space
            public float R;                               // capsule radius (metres)
            public string Tag;
        }

        sealed class CapSpec { public int Bone; public Vector3 A, B; public float R; }

        sealed class Section { public Mesh Mesh; public Material[] Mats; public string From; }

        sealed class HandSpec
        {
            public string Visual = "primitives";
            public readonly List<BoneSpec> Bones = new List<BoneSpec>();
            public readonly int[] Fin = { -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 };   // [finger*3 + joint], finger 0 = thumb
            public float Scale = 1f;                       // metres per root unit
            public Quaternion Align = Quaternion.identity; // wrist rotation = controller * offset * Align
            public readonly Vector3[] FAxis = new Vector3[5];   // curl axis per finger, root space
            public readonly List<ShapeSpec> Cols = new List<ShapeSpec>(), Prims = new List<ShapeSpec>();
            public readonly List<CapSpec> Caps = new List<CapSpec>();
            public readonly List<Section> Sections = new List<Section>();
            public string Note = "";
        }

        static int AddBone(HandSpec sp, string name, int parent, Vector3 posRoot)
        {
            var b = new BoneSpec { Name = name, Parent = parent, M = Matrix4x4.TRS(posRoot, Quaternion.identity, Vector3.one) };
            b.Pos = parent >= 0 ? posRoot - (Vector3)sp.Bones[parent].M.GetColumn(3) : posRoot;
            sp.Bones.Add(b);
            return sp.Bones.Count - 1;
        }

        // A hand with no game rig behind it: five fingers of three bones in a flat open pose, fingers along local +-X (the way a
        // wrist bone of a mirrored rig tends to point), thumb on +Z, palm toward -Y. The alignment makes the frame irrelevant.
        static HandSpec SyntheticSpec(bool left)
        {
            var sp = new HandSpec { Visual = "primitives" };
            float sx = left ? -1f : 1f;
            Vector3 fwd = new Vector3(sx, 0f, 0f), side = new Vector3(0f, 0f, 1f), nrm = new Vector3(0f, -1f, 0f);
            AddBone(sp, "Wrist", -1, Vector3.zero);
            string[] names = { "Thumb", "Index", "Middle", "Ring", "Pinky" };
            float[] across = { 0f, 0.030f, 0.010f, -0.011f, -0.031f };
            float[][] len = { new[] { 0.045f, 0.032f, 0.026f }, new[] { 0.040f, 0.024f, 0.020f }, new[] { 0.044f, 0.027f, 0.021f }, new[] { 0.041f, 0.025f, 0.019f }, new[] { 0.032f, 0.019f, 0.017f } };
            for (int f = 0; f < 5; f++)
            {
                Vector3 p, dir;
                if (f == 0) { p = fwd * 0.025f + side * 0.030f + nrm * 0.008f; dir = (fwd * 0.55f + side * 0.75f + nrm * 0.35f).normalized; }
                else { p = fwd * 0.088f + side * across[f]; dir = fwd; }
                int parent = 0;
                for (int j = 0; j < 3; j++)
                {
                    int bi = AddBone(sp, names[f] + (j + 1), parent, p);
                    sp.Fin[f * 3 + j] = bi;
                    parent = bi;
                    p += dir * len[f][j];
                }
            }
            string why = FinishSpec(sp, true, nrm);
            if (why != null) throw new Exception("synthetic hand: " + why);
            sp.Note = "synthetic";
            return sp;
        }

        // From the bones' root-space poses and the finger map: the palm frame, the alignment to the controller, the curl axes,
        // the colliders (palm box + 2 capsules per finger), the visual primitives and the soft-zone capsules.
        // Returns null when fine, else why not. Scale (metres per root unit) must be set already.
        static string FinishSpec(HandSpec sp, bool knownNormal, Vector3 normalV)
        {
            float u = sp.Scale;
            var P = new Vector3[5][];
            var cnt = new int[5];
            for (int f = 0; f < 5; f++)
            {
                P[f] = new Vector3[3];
                for (int j = 0; j < 3; j++)
                {
                    int bi = sp.Fin[f * 3 + j];
                    if (bi < 0) break;
                    P[f][j] = sp.Bones[bi].M.GetColumn(3);
                    cnt[f]++;
                }
            }
            Vector3 kn = Vector3.zero;
            int nk = 0, first = -1, last = -1;
            for (int f = 1; f < 5; f++) if (cnt[f] >= 1) { kn += P[f][0]; nk++; if (first < 0) first = f; last = f; }
            if (nk < 2) return "fewer than two finger chains";
            kn /= nk;
            if (kn.magnitude * u < 0.03f) return "palm too short (" + (kn.magnitude * u).ToString("0.000") + " m)";
            Vector3 fwd = kn.normalized;
            Vector3 across = P[first][0] - P[last][0];                  // pinky -> index: the thumb side
            across -= fwd * Vector3.Dot(across, fwd);
            if (across.magnitude * u < 0.01f) return "palm too narrow";
            across.Normalize();
            Vector3 n0 = Vector3.Cross(fwd, across).normalized;
            float width = (P[first][0] - P[last][0]).magnitude * u;

            // which way is the palm: knownNormal, else the fingers' rest curvature, else the thumb's side of the palm plane
            Vector3 normal; string how;
            if (knownNormal) { normal = normalV.normalized; how = "known"; }
            else
            {
                float sum = 0f; int nf = 0;
                for (int f = 1; f < 5; f++)
                {
                    if (cnt[f] < 3) continue;
                    var d = (P[f][1] - P[f][0]).normalized;
                    var v = P[f][2] - P[f][0];
                    v -= d * Vector3.Dot(v, d);
                    sum += Vector3.Dot(v, n0); nf++;
                }
                float mm = nf > 0 ? sum / nf * u * 1000f : 0f;
                float sign = 1f;
                if (Mathf.Abs(mm) >= 1f) { sign = mm > 0f ? 1f : -1f; how = "finger curvature " + mm.ToString("0.0") + " mm"; }
                else if (cnt[0] >= 2 && Mathf.Abs(Vector3.Dot(P[0][cnt[0] - 1], n0)) * u >= 0.003f)
                {
                    float t = Vector3.Dot(P[0][cnt[0] - 1], n0);
                    sign = t > 0f ? 1f : -1f; how = "thumb side " + (t * u * 1000f).ToString("0") + " mm";
                }
                else how = "assumed (open flat hand)";
                normal = n0 * sign;
            }
            sp.Note += (sp.Note.Length > 0 ? ", " : "") + "palm side by " + how;

            // Alignment (same construction as Embody's calibration): z along the fingers, y toward the thumb (pinky root -> thumb
            // root, squared to z); the wrist's rotation for a wanted frame F is F * Inverse(LookRotation(z, y)).
            Vector3 raw = across;
            if (cnt[0] >= 1)
            {
                var pk = P[last][0];
                var th = P[0][0] - pk;
                if (th.magnitude * u > 0.02f) raw = th;
            }
            raw -= fwd * Vector3.Dot(raw, fwd);
            if (raw.sqrMagnitude < 1e-10f) raw = across;
            raw.Normalize();
            sp.Align = Quaternion.Inverse(Quaternion.LookRotation(fwd, raw));

            // curl axes: rotating about cross(finger direction, palm normal) moves the tip toward the palm
            for (int f = 0; f < 5; f++)
            {
                Vector3 dir = cnt[f] >= 2 ? (P[f][cnt[f] - 1] - P[f][0]).normalized : fwd;
                Vector3 ax = Vector3.Cross(dir, normal);
                if (ax.sqrMagnitude < 1e-8f) ax = Vector3.Cross(fwd, normal);
                sp.FAxis[f] = ax.normalized;
            }

            // colliders: palm box + 2 capsules per finger (proximal; middle + distal to the tip)
            Vector3 centre = kn * 0.5f;
            Quaternion boxRot = Quaternion.LookRotation(fwd, normal);
            sp.Cols.Add(new ShapeSpec { Bone = 0, Box = true, A = centre, B = centre, Rot = boxRot, Size = new Vector3(width + 0.025f, 0.030f, kn.magnitude * u + 0.012f), Tag = "palm" });
            sp.Prims.Add(new ShapeSpec { Bone = 0, Box = true, A = centre, B = centre, Rot = boxRot, Size = new Vector3(width + 0.012f, 0.026f, kn.magnitude * u + 0.006f), Tag = "palm" });
            sp.Caps.Add(new CapSpec { Bone = 0, A = kn * 0.15f, B = kn * 0.9f, R = PalmR });
            string[] fn = { "thumb", "index", "middle", "ring", "pinky" };
            for (int f = 0; f < 5; f++)
            {
                int n = cnt[f];
                if (n < 1) continue;
                float r = f == 0 ? ThumbR : FingerR;
                Vector3 tip = n >= 2 ? P[f][n - 1] + (P[f][n - 1] - P[f][n - 2]) * 0.8f : P[f][0] + fwd * (0.02f / u);
                for (int j = 0; j < n; j++)
                {
                    Vector3 a = P[f][j], b = j + 1 < n ? P[f][j + 1] : tip;
                    int bi = sp.Fin[f * 3 + j];
                    sp.Prims.Add(new ShapeSpec { Bone = bi, A = a, B = b, R = f == 0 ? 0.011f : FingerVisR, Tag = fn[f] + (j + 1) });
                    sp.Caps.Add(new CapSpec { Bone = bi, A = a, B = b, R = FingerR });
                }
                if (n >= 2)
                {
                    sp.Cols.Add(new ShapeSpec { Bone = sp.Fin[f * 3], A = P[f][0], B = P[f][1], R = r, Tag = fn[f] + "A" });
                    sp.Cols.Add(new ShapeSpec { Bone = sp.Fin[f * 3 + 1], A = P[f][1], B = tip, R = r, Tag = fn[f] + "B" });
                }
                else sp.Cols.Add(new ShapeSpec { Bone = sp.Fin[f * 3], A = P[f][0], B = tip, R = r, Tag = fn[f] });
            }
            return null;
        }

        // ================= the game's hand: extracted from the player's body mesh =================

        static int FingerOf(string raw)
        {
            var n = Body.Norm(raw);
            if (n.Contains("thumb")) return 0;
            if (n.Contains("index")) return 1;
            if (n.Contains("middle")) return 2;
            if (n.Contains("pinky") || n.Contains("little")) return 4;
            // "ring" is also inside spring/string/earring: only as a finger name
            if (n.Contains("ringfinger") || n.StartsWith("ring", StringComparison.Ordinal) || (n.Contains("ring") && n.Contains("finger"))) return 3;
            return -1;
        }

        static void Decompose(Matrix4x4 m, out Vector3 pos, out Quaternion rot, out Vector3 scale)
        {
            pos = m.GetColumn(3);
            Vector3 c0 = m.GetColumn(0), c1 = m.GetColumn(1), c2 = m.GetColumn(2);
            scale = new Vector3(c0.magnitude, c1.magnitude, c2.magnitude);
            rot = c1.sqrMagnitude > 1e-12f && c2.sqrMagnitude > 1e-12f ? Quaternion.LookRotation(c2 / scale.z, c1 / scale.y) : Quaternion.identity;
        }

        // The player's hand skeleton + mesh for one side, or null with why. Kept bones K = Wrist_X's subtree + Wrist_X's parent
        // (driven rigid with the wrist); every K bone is posed in the wrist's space from the bind poses, Wrist.bind * bone.bind^-1.
        static HandSpec GameSpec(bool left, out string why)
        {
            why = null;
            var player = FindPlayer();
            if (player == null) { why = "no player in the scene"; return null; }
            var all = player.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            string wn = left ? "wristl" : "wristr";
            Transform wrist = null;
            foreach (var r in all)
            {
                if (r == null || r.sharedMesh == null || r.bones == null) continue;
                foreach (var b in r.bones) if (b != null && Body.Norm(b.name) == wn) { wrist = b; break; }
                if (wrist != null) break;
            }
            if (wrist == null) { why = "no Wrist_" + (left ? "L" : "R") + " among the player's skinned bones"; return null; }
            var cands = new List<SkinnedMeshRenderer>();
            foreach (var r in all)
                if (r != null && r.sharedMesh != null && r.bones != null && Array.IndexOf(r.bones, wrist) >= 0) cands.Add(r);
            var bodies = new List<SkinnedMeshRenderer>();
            foreach (var r in cands)
                if (r.name.ToLowerInvariant().Contains("body") || r.sharedMesh.name.ToLowerInvariant().Contains("body")) bodies.Add(r);
            var srcs = bodies.Count > 0 ? bodies : cands;

            // reference skin: the one holding the most K bones
            Transform forearmT = wrist.parent;
            SkinnedMeshRenderer refR = null; int most = -1;
            foreach (var r in srcs)
            {
                int c = 0;
                foreach (var b in r.bones) if (b != null && (b == wrist || b == forearmT || b.IsChildOf(wrist))) c++;
                if (c > most) { most = c; refR = r; }
            }
            Matrix4x4[] refBind;
            try { refBind = refR.sharedMesh.bindposes; } catch (Exception e) { why = "bind poses unreadable: " + e.Message; return null; }
            var bindOf = new Dictionary<Transform, Matrix4x4>();
            var rb = refR.bones;
            for (int i = 0; i < rb.Length && i < refBind.Length; i++) if (rb[i] != null && !bindOf.ContainsKey(rb[i])) bindOf[rb[i]] = refBind[i];

            var kT = new List<Transform>();
            kT.Add(wrist);
            bool hasForearm = forearmT != null && bindOf.ContainsKey(forearmT);
            if (hasForearm) kT.Add(forearmT);
            var stack = new Stack<Transform>();
            stack.Push(wrist);
            var order = new List<Transform>();
            while (stack.Count > 0)   // depth first, parents before children
            {
                var t = stack.Pop();
                for (int i = t.childCount - 1; i >= 0; i--) stack.Push(t.GetChild(i));
                if (t != wrist && bindOf.ContainsKey(t) && !IsUi(t)) order.Add(t);
            }
            kT.AddRange(order);
            var kIndex = new Dictionary<Transform, int>();
            for (int i = 0; i < kT.Count; i++) kIndex[kT[i]] = i;
            if (kT.Count < 8) { why = "only " + kT.Count + " hand bones with bind poses"; return null; }

            var sp = new HandSpec { Visual = "game hand" };
            Matrix4x4 bindW = bindOf[wrist];
            var rootM = new Matrix4x4[kT.Count];
            for (int i = 0; i < kT.Count; i++) rootM[i] = bindW * bindOf[kT[i]].inverse;
            for (int i = 0; i < kT.Count; i++)
            {
                var t = kT[i];
                int parent = -1;
                // nearest kept ancestor inside the wrist's subtree; the forearm copy (index 1) is a sibling of the wrist, nothing hangs below it
                if (i > 0 && !(hasForearm && i == 1))
                    for (var p = t.parent; p != null; p = p.parent)
                    {
                        int pi;
                        if (kIndex.TryGetValue(p, out pi) && !(hasForearm && pi == 1)) { parent = pi; break; }
                        if (p == wrist) break;
                    }
                var bs = new BoneSpec { Name = t.name, Parent = parent, M = rootM[i] };
                Vector3 lp, ls; Quaternion lr;
                Matrix4x4 local = parent >= 0 ? rootM[parent].inverse * rootM[i] : rootM[i];
                Decompose(local, out lp, out lr, out ls);
                bs.Pos = lp; bs.Rot = lr; bs.Scale = ls;
                sp.Bones.Add(bs);
            }

            // fingers: bones named like fingers below the wrist, one chain per finger by depth; the last three are the joints
            var groups = new List<int>[5];
            for (int f = 0; f < 5; f++) groups[f] = new List<int>();
            var depth = new int[kT.Count];
            for (int i = 0; i < kT.Count; i++)
            {
                int d = 0; for (var p = kT[i]; p != null && p != wrist; p = p.parent) d++;
                depth[i] = d;
            }
            for (int i = 1; i < kT.Count; i++)
            {
                if (hasForearm && i == 1) continue;
                int f = FingerOf(kT[i].name);
                if (f >= 0) groups[f].Add(i);
            }
            var fingerLog = new StringBuilder();
            for (int f = 0; f < 5; f++)
            {
                groups[f].Sort((a, b) => depth[a].CompareTo(depth[b]));
                var chain = new List<int>();
                int lastD = -1;
                foreach (var i in groups[f]) if (depth[i] > lastD) { chain.Add(i); lastD = depth[i]; }
                int start = Math.Max(0, chain.Count - 3);
                for (int j = 0; j < 3 && start + j < chain.Count; j++) sp.Fin[f * 3 + j] = chain[start + j];
                fingerLog.Append(f == 0 ? "" : ", ").Append(chain.Count);
            }
            int fingers = 0;
            for (int f = 0; f < 5; f++) if (sp.Fin[f * 3 + 1] >= 0) fingers++;
            if (fingers < 3) { why = "found finger chains: " + fingerLog + " (thumb..pinky), need at least three"; return null; }

            // size check: wrist -> middle fingertip should be a hand's length (0.15-0.23 m)
            int mi = -1;
            for (int j = 2; j >= 0; j--) if (sp.Fin[2 * 3 + j] >= 0) { mi = j; break; }
            float L;
            if (mi >= 1)
            {
                Vector3 pa = rootM[sp.Fin[6 + mi - 1]].GetColumn(3), pb = rootM[sp.Fin[6 + mi]].GetColumn(3);
                L = (pb + (pb - pa) * 0.8f).magnitude;
            }
            else { why = "middle finger has fewer than two bones"; return null; }
            float scale = 1f;
            string unitNote = "";
            if (L < 0.15f || L > 0.23f)
            {
                float ls = Mathf.Abs(refR.transform.lossyScale.x);
                scale = ls > 1e-6f ? ls : 1f;
                unitNote = " (hand " + L.ToString("0.###") + " units long, scaled by the renderer's " + scale.ToString("0.###") + ")";
                if (L * scale < 0.10f || L * scale > 0.30f) { why = "hand length " + (L * scale).ToString("0.000") + " m after unit scaling (" + L.ToString("0.###") + " units), not a hand"; return null; }
            }
            sp.Scale = scale;
            string fin = FinishSpec(sp, false, Vector3.zero);
            if (fin != null) { why = fin; return null; }

            // the mesh: weighted >= 0.5 on K and within 4 cm behind the wrist along the forearm axis
            Vector3 aRoot = hasForearm ? (-(Vector3)rootM[1].GetColumn(3)).normalized : (Vector3)(rootM[sp.Fin[6]].GetColumn(3)).normalized;
            if (aRoot.sqrMagnitude < 0.5f) aRoot = Vector3.forward;
            float back = 0.04f / scale;
            int totalV = 0, totalT = 0;
            var sb = new StringBuilder();
            foreach (var src in srcs)
            {
                string err;
                var d = MeshData.Read(src.sharedMesh, out err);
                if (d == null || d.Weights == null || d.Pos == null || d.Bind == null) { Plugin.Log.LogWarning("PhysicsHands: couldn't read '" + src.sharedMesh.name + "' (" + (err ?? "no bone weights") + ")"); continue; }
                int sv, st;
                var sec = Extract(src, d, kIndex, kT.Count, aRoot, back, out sv, out st);
                if (sec == null) continue;
                sp.Sections.Add(sec);
                totalV += sv; totalT += st;
                sb.Append(sb.Length > 0 ? ", " : "").Append(src.name).Append(' ').Append(sv).Append('v');
            }
            if (sp.Sections.Count == 0 || totalV < 30) { why = "no hand vertices in the player's skins (" + srcs.Count + " tried)"; foreach (var s in sp.Sections) if (s.Mesh != null) UnityEngine.Object.Destroy(s.Mesh); sp.Sections.Clear(); return null; }
            sp.Note = "game hand: " + totalV + " verts, " + totalT + " tris, " + kT.Count + " bones, fingers per chain " + fingerLog + ", hand " + (L * scale).ToString("0.000") + " m, scale " + scale.ToString("0.###") + unitNote + " [" + sb + "], " + sp.Note;
            return sp;
        }

        static void PutW(ref BoneWeight b, int slot, int idx, float w)
        {
            switch (slot)
            {
                case 0: b.boneIndex0 = idx; b.weight0 = w; break;
                case 1: b.boneIndex1 = idx; b.weight1 = w; break;
                case 2: b.boneIndex2 = idx; b.weight2 = w; break;
                default: b.boneIndex3 = idx; b.weight3 = w; break;
            }
        }

        // One source skin -> a hand mesh on the K bones: kept vertices (K weight >= .5, near the wrist), kept triangles (all three
        // kept), compacted; bone weights remapped to K and renormalised; one submesh per source submesh that kept triangles.
        static Section Extract(SkinnedMeshRenderer src, MeshData d, Dictionary<Transform, int> kIndex, int nK, Vector3 aRoot, float back, out int verts, out int tris)
        {
            verts = tris = 0;
            var bones = src.bones;
            var kOf = new int[bones.Length];
            var srcOfK = new int[nK];
            for (int i = 0; i < srcOfK.Length; i++) srcOfK[i] = -1;
            for (int i = 0; i < bones.Length; i++)
            {
                int k;
                kOf[i] = bones[i] != null && kIndex.TryGetValue(bones[i], out k) ? k : -1;
                if (kOf[i] >= 0 && srcOfK[kOf[i]] < 0) srcOfK[kOf[i]] = i;
            }
            if (srcOfK[0] < 0 || srcOfK[0] >= d.Bind.Length) return null;
            Matrix4x4 bw = d.Bind[srcOfK[0]];
            int nv = d.Pos.Length;
            if (d.Weights.Length != nv) return null;
            var kept = new bool[nv];
            for (int v = 0; v < nv; v++)
            {
                var w = d.Weights[v];
                float wk = 0f;
                if (w.weight0 > 0f && w.boneIndex0 < kOf.Length && kOf[w.boneIndex0] >= 0) wk += w.weight0;
                if (w.weight1 > 0f && w.boneIndex1 < kOf.Length && kOf[w.boneIndex1] >= 0) wk += w.weight1;
                if (w.weight2 > 0f && w.boneIndex2 < kOf.Length && kOf[w.boneIndex2] >= 0) wk += w.weight2;
                if (w.weight3 > 0f && w.boneIndex3 < kOf.Length && kOf[w.boneIndex3] >= 0) wk += w.weight3;
                if (wk < 0.5f) continue;
                if (Vector3.Dot(bw.MultiplyPoint3x4(d.Pos[v]), aRoot) < -back) continue;
                kept[v] = true;
            }
            var used = new bool[nv];
            var subs = new List<List<int>>();
            var subIdx = new List<int>();
            for (int s = 0; s < d.Sub.Count; s++)
            {
                var t = d.Sub[s];
                List<int> keep = null;
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    int a = t[i], b = t[i + 1], c = t[i + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= nv || b >= nv || c >= nv || !kept[a] || !kept[b] || !kept[c]) continue;
                    if (keep == null) keep = new List<int>();
                    keep.Add(a); keep.Add(b); keep.Add(c);
                    used[a] = used[b] = used[c] = true;
                }
                if (keep != null) { subs.Add(keep); subIdx.Add(s); }
            }
            var map = new int[nv];
            var order = new List<int>();
            for (int v = 0; v < nv; v++) { map[v] = used[v] ? order.Count : -1; if (used[v]) order.Add(v); }
            if (order.Count < 10) return null;

            int n = order.Count;
            var m = new Mesh { name = Pfx + "_hand_" + src.sharedMesh.name };
            if (n > 65535) m.indexFormat = IndexFormat.UInt32;
            var pos = new Vector3[n];
            for (int i = 0; i < n; i++) pos[i] = d.Pos[order[i]];
            m.vertices = pos;
            if (d.Nrm != null && d.Nrm.Length == nv) { var a = new Vector3[n]; for (int i = 0; i < n; i++) a[i] = d.Nrm[order[i]]; m.normals = a; }
            if (d.Tan != null && d.Tan.Length == nv) { var a = new Vector4[n]; for (int i = 0; i < n; i++) a[i] = d.Tan[order[i]]; m.tangents = a; }
            if (d.Uv != null && d.Uv.Length == nv) { var a = new Vector2[n]; for (int i = 0; i < n; i++) a[i] = d.Uv[order[i]]; m.uv = a; }
            for (int c = 1; c < 4; c++)
                if (d.Uvs[c] != null && d.Uvs[c].Count == nv) { var l = new List<Vector4>(n); for (int i = 0; i < n; i++) l.Add(d.Uvs[c][order[i]]); m.SetUVs(c, l); }
            if (d.Col != null && d.Col.Length == nv) { var a = new Color[n]; for (int i = 0; i < n; i++) a[i] = d.Col[order[i]]; m.colors = a; }
            var bwArr = new BoneWeight[n];
            for (int i = 0; i < n; i++)
            {
                var w = d.Weights[order[i]];
                var o = new BoneWeight();
                float tot = 0f; int slot = 0;
                for (int q = 0; q < 4; q++)
                {
                    int bi = q == 0 ? w.boneIndex0 : q == 1 ? w.boneIndex1 : q == 2 ? w.boneIndex2 : w.boneIndex3;
                    float wt = q == 0 ? w.weight0 : q == 1 ? w.weight1 : q == 2 ? w.weight2 : w.weight3;
                    if (wt <= 0f || bi < 0 || bi >= kOf.Length || kOf[bi] < 0) continue;
                    PutW(ref o, slot++, kOf[bi], wt); tot += wt;
                }
                if (tot > 0f) { o.weight0 /= tot; o.weight1 /= tot; o.weight2 /= tot; o.weight3 /= tot; }
                else { o.boneIndex0 = 0; o.weight0 = 1f; }
                bwArr[i] = o;
            }
            m.boneWeights = bwArr;
            var bind = new Matrix4x4[nK];
            for (int k = 0; k < nK; k++) bind[k] = srcOfK[k] >= 0 && srcOfK[k] < d.Bind.Length ? d.Bind[srcOfK[k]] : Matrix4x4.identity;
            m.bindposes = bind;
            m.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++)
            {
                var l = subs[s];
                var arr = new int[l.Count];
                for (int i = 0; i < arr.Length; i++) arr[i] = map[l[i]];
                m.SetTriangles(arr, s, false);
                tris += arr.Length / 3;
            }
            m.RecalculateBounds();
            var srcMats = src.sharedMaterials;
            var mats = new Material[subs.Count];
            for (int s = 0; s < subs.Count; s++) mats[s] = srcMats != null && srcMats.Length > 0 ? srcMats[Mathf.Min(subIdx[s], srcMats.Length - 1)] : null;
            verts = n;
            return new Section { Mesh = m, Mats = mats, From = src.name };
        }

        // ================= physics survey and layers =================

        static bool _surveyed, _layersApplied, _taken;
        static int _layerHand = -1, _layerHer = -1;
        static readonly bool[] _origHand = new bool[32], _origHer = new bool[32];
        static float _retryAt;

        // Why physics can't be used, or null. Logs the facts once per session.
        static string Prepare()
        {
            float dt = Time.fixedDeltaTime;
            var mode = Physics.simulationMode;
            var used = new bool[32];
            int colliders = 0;
            foreach (var o in Resources.FindObjectsOfTypeAll(typeof(Collider)))
            {
                var c = o as Collider;
                if (c == null || !c.gameObject.scene.IsValid()) continue;
                used[c.gameObject.layer] = true; colliders++;
            }
            var names = new StringBuilder();
            for (int i = 0; i < 32; i++) { var n = LayerMask.LayerToName(i); if (n.Length > 0) names.Append(names.Length > 0 ? ", " : "").Append(i).Append('=').Append(n); }
            var usedS = new StringBuilder();
            for (int i = 0; i < 32; i++) if (used[i]) usedS.Append(usedS.Length > 0 ? "," : "").Append(i);
            if (!_surveyed)
            {
                _surveyed = true;
                Plugin.Log.LogInfo("PhysicsHands survey: fixedDeltaTime " + dt.ToString("0.#####") + " (" + (dt > 0f ? 1f / dt : 0f).ToString("0.#") + " Hz), timeScale " + Time.timeScale
                                   + ", simulationMode " + mode + ", solver iterations " + Physics.defaultSolverIterations + "/" + Physics.defaultSolverVelocityIterations
                                   + ", gravity " + Physics.gravity + ", " + colliders + " colliders in the scene on layers [" + usedS + "], named layers [" + names + "]");
            }
            if (!(dt > 0f)) return "fixedDeltaTime is " + dt;
            if (mode != SimulationMode.FixedUpdate) return "physics doesn't simulate automatically (simulationMode " + mode + ")";

            int a = -1, b = -1;
            for (int i = 31; i >= 8; i--)
            {
                if (used[i] || LayerMask.LayerToName(i).Length > 0) continue;
                if (a < 0) a = i; else { b = i; break; }
            }
            if (b < 0) return "no two unused physics layers (colliders sit on [" + usedS + "])";
            _layerHand = a; _layerHer = b;
            for (int k = 0; k < 32; k++)
            {
                _origHand[k] = Physics.GetIgnoreLayerCollision(_layerHand, k);
                _origHer[k] = Physics.GetIgnoreLayerCollision(_layerHer, k);
            }
            for (int k = 0; k < 32; k++) Physics.IgnoreLayerCollision(_layerHand, k, k != _layerHer);
            for (int k = 0; k < 32; k++) Physics.IgnoreLayerCollision(_layerHer, k, k != _layerHand);
            _layersApplied = true; _taken = true;
            Plugin.Log.LogInfo("PhysicsHands: layers " + _layerHand + " (hands) and " + _layerHer + " (her body): each collides only with the other; the earlier matrix is restored on release");
            return null;
        }

        static void RestoreLayers()
        {
            if (!_layersApplied) return;
            for (int k = 0; k < 32; k++) Physics.IgnoreLayerCollision(_layerHand, k, _origHand[k]);
            for (int k = 0; k < 32; k++) Physics.IgnoreLayerCollision(_layerHer, k, _origHer[k]);
            _layersApplied = false;
        }

        // ================= a hand =================

        sealed class Hand
        {
            public bool Left, Fallback, Kin, CollideOn = true;
            public string FallbackWhy = "";
            public HandSpec Spec;
            public GameObject PGo, VGo;
            public Transform PRoot, VRoot;
            public Transform[] PBones, VBones;
            public Rigidbody Rb;
            public readonly List<Transform> ColT = new List<Transform>();
            public readonly List<Collider> Cols = new List<Collider>();
            public readonly List<Renderer> Rends = new List<Renderer>();
            public readonly List<Mesh> Meshes = new List<Mesh>();
            public readonly List<GameObject> Marks = new List<GameObject>();
            public Quaternion[] Rest = new Quaternion[15];
            public Vector3[] AxisLocal = new Vector3[15];
            public float Curl, AppliedCurl = -1f;
            public int BuiltMesh;
            // the target used at the last fixed step (for the visual offset) and the teleport rules
            public bool HaveFixed, HaveLast;
            public Vector3 FixedPos, LastTarget;
            public Quaternion FixedRot = Quaternion.identity;
            public float FarTime, NextUpgrade, NextMat;
            // grip
            public int GripKind;                  // 0 none, 1 VR_HandController field, 2 controller Animator parameter, 3 XR
            public string GripDesc = "none";
            public float NextGrip;
            public Component GripOwner; public FieldInfo GripField; public Func<object, float> GripGet;
            public Animator GripAnim; public int GripHash;
            public object XrDev; public readonly object[] XrArgs = new object[2]; public int XrFail;
            public bool GripWarned;
        }

        static Hand _left, _right;
        static HandsSettings _s;

        static void Target(Transform c, Vector3 off, Vector3 rot, Quaternion align, out Vector3 p, out Quaternion q)
        {
            var cr = c.rotation;
            p = c.position + cr * off;                       // the wrist, in the controller's axes
            q = cr * Quaternion.Euler(rot) * align;          // the extra rotation, on top of the automatic hand alignment
        }

        static void SetLayerRec(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRec(t.GetChild(i), layer);
        }

        static Transform[] MakeBones(Transform root, HandSpec sp)
        {
            var t = new Transform[sp.Bones.Count];
            for (int i = 0; i < t.Length; i++)
            {
                var b = sp.Bones[i];
                var tr = new GameObject(b.Name).transform;
                tr.SetParent(b.Parent >= 0 ? t[b.Parent] : root, false);
                tr.localPosition = b.Pos; tr.localRotation = b.Rot; tr.localScale = b.Scale;
                t[i] = tr;
            }
            return t;
        }

        static Material _skinMat;
        static Material SkinMat()
        {
            if (_skinMat != null) return _skinMat;
            var sh = Shader.Find("HDRP/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            if (sh == null)
            {
                var prim = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _skinMat = new Material(prim.GetComponent<Renderer>().sharedMaterial);
                UnityEngine.Object.Destroy(prim);
            }
            else _skinMat = new Material(sh);
            _skinMat.name = Pfx + "_skin";
            var tone = new Color(0.86f, 0.66f, 0.55f, 1f);
            if (_skinMat.HasProperty("_BaseColor")) _skinMat.SetColor("_BaseColor", tone);
            if (_skinMat.HasProperty("_Color")) _skinMat.SetColor("_Color", tone);
            if (_skinMat.HasProperty("_Smoothness")) _skinMat.SetFloat("_Smoothness", 0.35f);
            return _skinMat;
        }

        // Chooses the hand's spec (log which visual) and builds it at the controller's pose; null if it can't.
        static Hand MakeHand(bool left, Transform ctl, HandsSettings s, HandSpec given)
        {
            HandSpec sp = given;
            bool fallback = false; string why = "";
            if (sp == null && s.Mesh == 0)
            {
                try { sp = GameSpec(left, out why); }
                catch (Exception e) { why = e.GetType().Name + ": " + e.Message; sp = null; }
                if (sp == null) { fallback = true; LogOnce("fb" + left, (left ? "left" : "right") + " hand: game hand unavailable (" + why + "), primitive hand instead"); }
            }
            if (sp == null) sp = SyntheticSpec(left);
            var h = new Hand { Left = left, Spec = sp, Fallback = fallback, FallbackWhy = why, BuiltMesh = s.Mesh };
            h.NextUpgrade = Time.unscaledTime + 10f;
            try { BuildHand(h, ctl, s); }
            catch (Exception e)
            {
                Plugin.Log.LogError("PhysicsHands: building the " + (left ? "left" : "right") + " hand failed: " + e);
                DestroyHand(h);
                return null;
            }
            Plugin.Log.LogInfo("PhysicsHands: " + (left ? "left" : "right") + " hand: " + sp.Visual + ", " + sp.Cols.Count + " colliders, " + sp.Note);
            return h;
        }

        static void BuildHand(Hand h, Transform ctl, HandsSettings s)
        {
            var sp = h.Spec;
            Vector3 tp; Quaternion tq;
            Target(ctl, h.Left ? s.LeftPos : s.RightPos, h.Left ? s.LeftRot : s.RightRot, sp.Align, out tp, out tq);
            string side = h.Left ? "L" : "R";

            // the physics body: bones (they curl) with a child per collider
            h.PGo = new GameObject(Pfx + "_" + side + "_body");
            h.PRoot = h.PGo.transform;
            h.PRoot.SetPositionAndRotation(tp, tq);
            h.PRoot.localScale = Vector3.one * sp.Scale;
            h.PBones = MakeBones(h.PRoot, sp);
            foreach (var sh in sp.Cols)
            {
                var bone = h.PBones[sh.Bone];
                var go = new GameObject(Pfx + "_col_" + side + "_" + sh.Tag);
                go.transform.SetParent(bone, false);
                Collider col;
                if (sh.Box)
                {
                    go.transform.position = h.PRoot.TransformPoint(sh.A);
                    go.transform.rotation = h.PRoot.rotation * sh.Rot;
                    var bc = go.AddComponent<BoxCollider>();
                    bc.size = sh.Size / sp.Scale;
                    col = bc;
                }
                else
                {
                    Vector3 a = h.PRoot.TransformPoint(sh.A), b = h.PRoot.TransformPoint(sh.B);
                    Vector3 d = b - a; float len = d.magnitude;
                    go.transform.position = (a + b) * 0.5f;
                    go.transform.rotation = len > 1e-6f ? Quaternion.FromToRotation(Vector3.up, d / len) : Quaternion.identity;
                    var cc = go.AddComponent<CapsuleCollider>();
                    cc.direction = 1;
                    cc.radius = sh.R / sp.Scale;
                    cc.height = (len + 2f * sh.R) / sp.Scale;
                    col = cc;
                }
                h.ColT.Add(go.transform); h.Cols.Add(col);
            }
            SetLayerRec(h.PRoot, _layerHand);
            var rb = h.PGo.AddComponent<Rigidbody>();
            rb.mass = 1f; rb.useGravity = false; rb.drag = 0f; rb.angularDrag = 0f;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.maxAngularVelocity = 50f; rb.maxDepenetrationVelocity = 2f;
            rb.solverIterations = 12; rb.sleepThreshold = 0f;
            rb.centerOfMass = Vector3.zero;   // so the velocity we command is the wrist's
            h.Rb = rb;
            h.PGo.AddComponent<PhysicsHandProbe>().Left = h.Left;

            // the visual: the same skeleton, posed at the (offset) controller target each frame
            h.VGo = new GameObject(Pfx + "_" + side + "_visual");
            h.VRoot = h.VGo.transform;
            h.VRoot.SetPositionAndRotation(tp, tq);
            h.VRoot.localScale = Vector3.one * sp.Scale;
            h.VBones = MakeBones(h.VRoot, sp);
            if (sp.Sections.Count > 0)
            {
                foreach (var sec in sp.Sections)
                {
                    var go = new GameObject(Pfx + "_mesh_" + sec.From);
                    go.transform.SetParent(h.VRoot, false);
                    var smr = go.AddComponent<SkinnedMeshRenderer>();
                    smr.sharedMesh = sec.Mesh;
                    smr.bones = h.VBones;
                    smr.rootBone = h.VBones[0];
                    var mats = new Material[sec.Mats.Length];
                    for (int i = 0; i < mats.Length; i++) mats[i] = sec.Mats[i] != null ? sec.Mats[i] : SkinMat();
                    smr.sharedMaterials = mats;
                    smr.updateWhenOffscreen = true;
                    smr.quality = SkinQuality.Bone4;
                    h.Rends.Add(smr);
                    h.Meshes.Add(sec.Mesh);
                }
            }
            else
            {
                foreach (var sh in sp.Prims)
                {
                    var go = GameObject.CreatePrimitive(sh.Box ? PrimitiveType.Cube : PrimitiveType.Capsule);
                    go.name = Pfx + "_prim_" + sh.Tag;
                    foreach (var comp in go.GetComponents<Component>())   // a visual must never be touched or touch anything
                        if (comp != null && comp.GetType().Name.EndsWith("Collider", StringComparison.Ordinal)) UnityEngine.Object.DestroyImmediate(comp);
                    var bone = h.VBones[sh.Bone];
                    go.transform.SetParent(bone, false);
                    if (sh.Box)
                    {
                        go.transform.position = h.VRoot.TransformPoint(sh.A);
                        go.transform.rotation = h.VRoot.rotation * sh.Rot;
                        go.transform.localScale = sh.Size / sp.Scale;
                    }
                    else
                    {
                        Vector3 a = h.VRoot.TransformPoint(sh.A), b = h.VRoot.TransformPoint(sh.B);
                        Vector3 d = b - a; float len = d.magnitude;
                        go.transform.position = (a + b) * 0.5f;
                        go.transform.rotation = len > 1e-6f ? Quaternion.FromToRotation(Vector3.up, d / len) : Quaternion.identity;
                        go.transform.localScale = new Vector3(2f * sh.R, (len + 2f * sh.R) * 0.5f, 2f * sh.R) / sp.Scale;
                    }
                    var r = go.GetComponent<Renderer>();
                    r.sharedMaterial = SkinMat();
                    r.shadowCastingMode = ShadowCastingMode.On;
                    h.Rends.Add(r);
                }
            }

            // rest rotations and curl axes (in each bone's own axes)
            for (int f = 0; f < 5; f++)
                for (int j = 0; j < 3; j++)
                {
                    int i = f * 3 + j, bi = sp.Fin[i];
                    if (bi < 0) { h.Rest[i] = Quaternion.identity; h.AxisLocal[i] = Vector3.right; continue; }
                    Vector3 p0, sc; Quaternion rr;
                    Decompose(sp.Bones[bi].M, out p0, out rr, out sc);
                    h.Rest[i] = sp.Bones[bi].Rot;
                    h.AxisLocal[i] = Quaternion.Inverse(rr) * sp.FAxis[f];
                }
            h.Kin = false;
            h.CollideOn = true;
            if (_dbgOn) AddMarkers(h);
            _statusDirty = true;
        }

        static void DestroyHand(Hand h)
        {
            if (h == null) return;
            RemoveMarkers(h);
            if (h.PGo != null) UnityEngine.Object.Destroy(h.PGo);
            if (h.VGo != null) UnityEngine.Object.Destroy(h.VGo);
            foreach (var m in h.Meshes) if (m != null) UnityEngine.Object.Destroy(m);
            h.Meshes.Clear(); h.Rends.Clear(); h.Cols.Clear(); h.ColT.Clear();
            foreach (var sec in h.Spec.Sections) if (sec.Mesh != null) UnityEngine.Object.Destroy(sec.Mesh);
            h.PGo = h.VGo = null;
        }

        // The visual capsules for the soft zones: palm + finger segments on the visual bones, radii palm .035 fingers .011.
        static void RebuildCaps()
        {
            bool had = Capsules.Count > 0;
            Capsules.Clear();
            LeftWrist = RightWrist = null;
            foreach (var h in new[] { _left, _right })
            {
                if (h == null || h.VGo == null) continue;
                if (h.Left) LeftWrist = h.VBones[0]; else RightWrist = h.VBones[0];
                foreach (var c in h.Spec.Caps)
                {
                    var bone = h.VBones[c.Bone];
                    Vector3 a = h.VRoot.TransformPoint(c.A), b = h.VRoot.TransformPoint(c.B);
                    Capsules.Add(new SoftCapsule { T = bone, LocalA = bone.InverseTransformPoint(a), LocalB = bone.InverseTransformPoint(b), Radius = c.R, Owner = "PhysicsHands", Kind = "vr" });
                }
            }
            if (had || Capsules.Count > 0) Version++;
            Active = _left != null || _right != null;
        }

        // ================= grip =================

        static Func<object, float> MakeGetter(FieldInfo f)
        {
            var t = f.FieldType;
            if (t != typeof(float) && t != typeof(double) && t != typeof(int) && t != typeof(bool)) return null;
            try
            {
                // a compiled getter, so reading the grip every frame doesn't box
                var dm = new DynamicMethod("sfm_grip_" + f.Name, typeof(float), new[] { typeof(object) }, typeof(PhysicsHands).Module, true);
                var il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, f.DeclaringType);
                il.Emit(OpCodes.Ldfld, f);
                if (t != typeof(float)) il.Emit(OpCodes.Conv_R4);
                il.Emit(OpCodes.Ret);
                return (Func<object, float>)dm.CreateDelegate(typeof(Func<object, float>));
            }
            catch (Exception) { return null; }
        }

        static bool IsNumberType(Type t) { return t == typeof(float) || t == typeof(double) || t == typeof(int) || t == typeof(bool); }

        static bool SideName(string n, bool left)
        {
            n = n.ToLowerInvariant();
            return left ? n.Contains("left") || n.EndsWith("_l", StringComparison.Ordinal) : n.Contains("right") || n.EndsWith("_r", StringComparison.Ordinal);
        }

        // Source order: (1) VR_HandController.selectValue_left/right (or a field named grip*), (2) the controller Animator's
        // grip/select/trigger float parameter, (3) XR InputDevices grip through reflection. Logged when chosen; retried every 2 s.
        static void ResolveGrip(Hand h)
        {
            h.GripKind = 0; h.GripDesc = "none"; h.GripField = null; h.GripGet = null; h.GripAnim = null; h.XrDev = null;
            var hc = HandController();
            if (hc != null)
            {
                FieldInfo best = null; int bestScore = 0;
                var cand = new StringBuilder();
                foreach (var f in _handType.GetFields(Any))
                {
                    if (f.IsStatic || !IsNumberType(f.FieldType)) continue;
                    var ln = f.Name.ToLowerInvariant();
                    if (ln.Contains("select") || ln.Contains("grip") || ln.Contains("trigger")) cand.Append(cand.Length > 0 ? ", " : "").Append(f.Name);
                    if (!SideName(f.Name, h.Left)) continue;
                    int score = ln.Contains("selectvalue") ? 3 : ln.Contains("grip") ? 2 : 0;
                    if (score > bestScore) { best = f; bestScore = score; }
                }
                if (!h.GripWarned && cand.Length > 0) LogOnce("gripfields", "VR_HandController numeric fields about select/grip/trigger: " + cand);
                if (best != null)
                {
                    h.GripKind = 1; h.GripOwner = hc; h.GripField = best; h.GripGet = MakeGetter(best);
                    h.GripDesc = "VR_HandController." + best.Name;
                }
                else
                {
                    var anim = Get(hc, h.Left ? "controller_left" : "controller_right") as Animator;
                    if (anim != null)
                    {
                        string pick = null; int sc = 0;
                        try
                        {
                            foreach (var p in anim.parameters)
                            {
                                if (p.type != AnimatorControllerParameterType.Float) continue;
                                var ln = p.name.ToLowerInvariant();
                                int score = ln.Contains("grip") ? 3 : ln.Contains("select") ? 2 : ln.Contains("trigger") ? 1 : 0;
                                if (score > sc) { sc = score; pick = p.name; }
                            }
                        }
                        catch (Exception) { }
                        if (pick != null)
                        {
                            h.GripKind = 2; h.GripAnim = anim; h.GripHash = Animator.StringToHash(pick);
                            h.GripDesc = "controller Animator parameter '" + pick + "'";
                        }
                    }
                }
            }
            if (h.GripKind == 0 && XrInit())
            {
                try
                {
                    var dev = _xrGetAt.Invoke(null, new object[] { Enum.ToObject(_xrNode, h.Left ? 4 : 5) });
                    var valid = dev.GetType().GetProperty("isValid").GetValue(dev, null);
                    if (valid is bool && (bool)valid)
                    {
                        h.XrDev = dev; h.XrFail = 0;
                        h.GripKind = 3; h.GripDesc = "XR InputDevices grip";
                    }
                }
                catch (Exception) { }
            }
            if (h.GripKind != 0) Plugin.Log.LogInfo("PhysicsHands: " + (h.Left ? "left" : "right") + " grip from " + h.GripDesc);
            else if (!h.GripWarned) { h.GripWarned = true; Plugin.Log.LogInfo("PhysicsHands: " + (h.Left ? "left" : "right") + " grip: no source found yet (hand stays open, retrying every 2 s)"); }
            _statusDirty = true;
        }

        static int _xrState;   // 0 not looked, 1 usable, 2 not
        static Type _xrNode;
        static MethodInfo _xrGetAt, _xrTry;
        static object _xrUsage;

        static bool XrInit()
        {
            if (_xrState != 0) return _xrState == 1;
            _xrState = 2;
            try
            {
                Type di = null, node = null, cu = null, dev = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!a.GetName().Name.StartsWith("UnityEngine", StringComparison.Ordinal)) continue;
                    di = di ?? a.GetType("UnityEngine.XR.InputDevices");
                    node = node ?? a.GetType("UnityEngine.XR.XRNode");
                    cu = cu ?? a.GetType("UnityEngine.XR.CommonUsages");
                    dev = dev ?? a.GetType("UnityEngine.XR.InputDevice");
                }
                if (di == null || node == null || cu == null || dev == null) return false;
                _xrGetAt = di.GetMethod("GetDeviceAtXRNode", BindingFlags.Public | BindingFlags.Static);
                var grip = cu.GetField("grip", BindingFlags.Public | BindingFlags.Static);
                if (_xrGetAt == null || grip == null) return false;
                _xrUsage = grip.GetValue(null);
                foreach (var m in dev.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "TryGetFeatureValue") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == grip.FieldType && ps[1].ParameterType == typeof(float).MakeByRefType()) { _xrTry = m; break; }
                }
                if (_xrTry == null) return false;
                _xrNode = node;
                _xrState = 1;
            }
            catch (Exception) { _xrState = 2; }
            return _xrState == 1;
        }

        static float ReadGrip(Hand h, float now)
        {
            switch (h.GripKind)
            {
                case 1:
                    if (h.GripOwner == null) { h.GripKind = 0; break; }
                    try { return Mathf.Clamp01(h.GripGet != null ? h.GripGet(h.GripOwner) : Convert.ToSingle(h.GripField.GetValue(h.GripOwner))); }
                    catch (Exception) { h.GripKind = 0; break; }
                case 2:
                    if (h.GripAnim == null) { h.GripKind = 0; break; }
                    return Mathf.Clamp01(h.GripAnim.GetFloat(h.GripHash));
                case 3:
                    try
                    {
                        h.XrArgs[0] = _xrUsage; h.XrArgs[1] = 0f;   // a few small boxes per call: only on this last-resort source
                        if ((bool)_xrTry.Invoke(h.XrDev, h.XrArgs)) { h.XrFail = 0; return Mathf.Clamp01((float)h.XrArgs[1]); }
                        if (++h.XrFail > 120) h.GripKind = 0;
                        return h.Curl;
                    }
                    catch (Exception) { h.GripKind = 0; break; }
            }
            if (now >= h.NextGrip) { h.NextGrip = now + 2f; ResolveGrip(h); }
            return 0f;
        }

        // ================= controller models =================

        sealed class Hider
        {
            public readonly List<Renderer> R = new List<Renderer>();
            public readonly List<bool> Was = new List<bool>();
            public readonly HashSet<Renderer> Seen = new HashSet<Renderer>();
            public float NextScan;
        }

        static readonly Hider _hideL = new Hider(), _hideR = new Hider();

        // Renderers under VR_HandController.controller_left/right (not UI, not line renderers) off, re-asserted every frame.
        static void HideController(Hider hd, bool left, float now)
        {
            if (now >= hd.NextScan)
            {
                hd.NextScan = now + 2f;
                var hc = HandController();
                var anim = hc != null ? Get(hc, left ? "controller_left" : "controller_right") as Animator : null;
                if (anim != null)
                {
                    int before = hd.R.Count;
                    foreach (var r in anim.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null || r is LineRenderer || IsUi(r.transform) || hd.Seen.Contains(r)) continue;
                        hd.Seen.Add(r); hd.R.Add(r); hd.Was.Add(r.enabled);
                    }
                    if (hd.R.Count != before) Plugin.Log.LogInfo("PhysicsHands: hiding " + hd.R.Count + " controller renderers on " + (left ? "controller_left" : "controller_right"));
                }
                else LogOnce("nohc" + left, "no controller model found under VR_HandController." + (left ? "controller_left" : "controller_right") + ": nothing to hide");
            }
            for (int i = 0; i < hd.R.Count; i++)
            {
                var r = hd.R[i];
                if (r != null && r.enabled) r.enabled = false;
            }
        }

        static void RestoreHide(Hider hd)
        {
            for (int i = 0; i < hd.R.Count; i++) if (hd.R[i] != null) hd.R[i].enabled = hd.Was[i];
            hd.R.Clear(); hd.Was.Clear(); hd.Seen.Clear(); hd.NextScan = 0f;
        }

        // ================= her body: kinematic capsules =================

        sealed class Seg
        {
            public string Name, Owner;
            public Transform A, B;                  // B null: a sphere around LocalCenter (in A's space)
            public bool Soft, Sphere;
            public Vector3 LocalCenter;
            public float RSkin, RDef, RBase, Ratio = 1f, BuildScale = 1f, H;
            public int Verts;
            public GameObject Go; public Transform T; public Rigidbody Rb; public CapsuleCollider Col;
            public Vector3 LastPos; public bool Placed;
            public GameObject Mark;
        }

        sealed class SegDef
        {
            public string Name, A, B, Owner; public bool Soft, AllDesc; public float Def;
            public SegDef(string n, string a, string b, string o, bool soft, float def, bool all = false) { Name = n; A = a; B = b; Owner = o; Soft = soft; Def = def; AllDesc = all; }
        }

        static readonly SegDef[] HerDefs =
        {
            new SegDef("pelvis", "Hip_L", "Hip_R", "Root_M", true, 0.10f),
            new SegDef("spine", "Spine1_M", "Chest_M", "Spine1_M", true, 0.10f),
            new SegDef("chest", "Chest_M", "Neck_M", "Chest_M", true, 0.11f),
            new SegDef("neck", "Neck_M", "Head_M", "Neck_M", false, 0.05f),
            new SegDef("head", "Head_M", null, "Head_M", false, 0.095f, true),
            new SegDef("thigh_L", "Hip_L", "Knee_L", "Hip_L", true, 0.085f),
            new SegDef("thigh_R", "Hip_R", "Knee_R", "Hip_R", true, 0.085f),
            new SegDef("calf_L", "Knee_L", "Ankle_L", "Knee_L", false, 0.055f),
            new SegDef("calf_R", "Knee_R", "Ankle_R", "Knee_R", false, 0.055f),
            new SegDef("upperarm_L", "Shoulder_L", "Elbow_L", "Shoulder_L", false, 0.045f),
            new SegDef("upperarm_R", "Shoulder_R", "Elbow_R", "Shoulder_R", false, 0.045f),
            new SegDef("forearm_L", "Elbow_L", "Wrist_L", "Elbow_L", false, 0.035f),
            new SegDef("forearm_R", "Elbow_R", "Wrist_R", "Elbow_R", false, 0.035f),
            new SegDef("butt_L", "Butt_L", null, "Butt_L", true, 0.09f),
            new SegDef("butt_R", "Butt_R", null, "Butt_R", true, 0.09f),
        };

        static readonly List<Seg> _segs = new List<Seg>();
        static GameObject _herGo;
        static Transform _herRoot;
        static bool _herBuilt, _herDirty;
        static float _herNext, _herSoftness = -1f, _herUnit = 1f;
        static string _herNote = "";

        static Transform FindBone(Character her, string name)
        {
            Transform t;
            if (her.Bones.TryGetValue(name, out t) && t != null) return t;
            foreach (var c in her.Root.GetComponentsInChildren<Transform>(true))
                if (c != null && c.name == name && !c.name.StartsWith(SoftZones.Prefix, StringComparison.Ordinal)) return c;
            return null;
        }

        static string Stem(string n)
        {
            if (n.EndsWith("_L", StringComparison.Ordinal) || n.EndsWith("_R", StringComparison.Ordinal) || n.EndsWith("_M", StringComparison.Ordinal)) n = n.Substring(0, n.Length - 2);
            return n;
        }

        // The bone and the helper bones of the same part below it (HipPart1_L under Hip_L), or everything below it.
        static void Owned(Transform t, string stem, bool all, HashSet<Transform> into)
        {
            into.Add(t);
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c.name.StartsWith(SoftZones.Prefix, StringComparison.Ordinal)) continue;
                if (all || c.name.StartsWith(stem, StringComparison.Ordinal)) Owned(c, stem, all, into);
            }
        }

        static float Inset(Seg g, float softness) { return g.Soft ? Mathf.Lerp(0.02f, 0.045f, Mathf.Clamp01(softness)) : 0.008f; }

        static void BuildHer(Character her, HandsSettings s)
        {
            DestroyHer();
            _herRoot = her.Root;
            _herDirty = false;
            var segs = new List<Seg>();
            foreach (var d in HerDefs)
            {
                var a = FindBone(her, d.A);
                var b = d.B != null ? FindBone(her, d.B) : null;
                if (a == null || (d.B != null && b == null)) { LogOnce("hermiss" + d.Name, "her collider " + d.Name + ": bone " + (a == null ? d.A : d.B) + " not found, skipped"); continue; }
                segs.Add(new Seg { Name = d.Name, Owner = d.Owner, A = a, B = b, Sphere = d.B == null, Soft = d.Soft, RDef = d.Def, RSkin = d.Def });
            }
            if (segs.Count < 4) { _herNote = "her: only " + segs.Count + " bones found"; _herNext = Time.unscaledTime + 10f; _herRoot = null; return; }

            // skin radii from her body meshes: weights >= .5 on the part's bones, distance to the segment axis (bind space)
            var posOf = new Dictionary<Transform, Vector3>();
            var bindOfBone = new Dictionary<Transform, Matrix4x4>();
            foreach (var r in her.Renderers)
            {
                if (r == null || r.sharedMesh == null) continue;
                var bs = r.bones; Matrix4x4[] bp;
                try { bp = r.sharedMesh.bindposes; } catch (Exception) { continue; }
                for (int i = 0; i < bs.Length && i < bp.Length; i++)
                    if (bs[i] != null && !posOf.ContainsKey(bs[i])) { posOf[bs[i]] = bp[i].inverse.MultiplyPoint3x4(Vector3.zero); bindOfBone[bs[i]] = bp[i]; }
            }
            // mesh units -> metres: the thigh's length in the bind pose against its length now (only trusted when clearly different units)
            _herUnit = 1f;
            Vector3 pa, pb;
            var hipL = FindBone(her, "Hip_L"); var kneeL = FindBone(her, "Knee_L");
            if (hipL != null && kneeL != null && posOf.TryGetValue(hipL, out pa) && posOf.TryGetValue(kneeL, out pb) && (pa - pb).magnitude > 1e-4f)
            {
                float ratio = (hipL.position - kneeL.position).magnitude / (pa - pb).magnitude;
                if (Mathf.Abs(ratio - 1f) > 0.5f) _herUnit = ratio;
                Plugin.Log.LogInfo("PhysicsHands: her skin units: thigh " + (pa - pb).magnitude.ToString("0.###") + " in the bind pose, " + (hipL.position - kneeL.position).magnitude.ToString("0.###") + " m now -> factor " + _herUnit.ToString("0.###"));
            }
            var segOf = new Dictionary<Transform, int>();
            for (int i = 0; i < segs.Count; i++)
            {
                var owner = FindBone(her, segs[i].Owner);
                if (owner == null) continue;
                var set = new HashSet<Transform>();
                Owned(owner, Stem(owner.name), Array.Find(HerDefs, q => q.Name == segs[i].Name).AllDesc, set);
                foreach (var t in set) if (!segOf.ContainsKey(t)) segOf[t] = i;
            }
            var pts = new List<Vector3>[segs.Count];
            for (int i = 0; i < pts.Length; i++) pts[i] = new List<Vector3>();
            int bodies = 0;
            foreach (var r in her.Renderers)
            {
                if (r == null || r.sharedMesh == null || !r.sharedMesh.name.ToLowerInvariant().Contains("body")) continue;
                string err;
                var d = MeshData.Read(r.sharedMesh, out err);
                if (d == null || d.Weights == null || d.Pos == null) { Plugin.Log.LogWarning("PhysicsHands: her skin '" + r.sharedMesh.name + "' unreadable (" + (err ?? "no weights") + ")"); continue; }
                bodies++;
                var bs = r.bones;
                var segOfIdx = new int[bs.Length];
                for (int i = 0; i < bs.Length; i++)
                {
                    var t = bs[i];
                    while (t != null && t.name.StartsWith(SoftZones.Prefix, StringComparison.Ordinal)) t = t.parent;   // helper bones count for their real bone
                    int si;
                    segOfIdx[i] = t != null && segOf.TryGetValue(t, out si) ? si : -1;
                }
                var acc = new float[segs.Count];
                for (int v = 0; v < d.Pos.Length && v < d.Weights.Length; v++)
                {
                    Array.Clear(acc, 0, acc.Length);
                    var w = d.Weights[v];
                    if (w.boneIndex0 < segOfIdx.Length && segOfIdx[w.boneIndex0] >= 0) acc[segOfIdx[w.boneIndex0]] += w.weight0;
                    if (w.boneIndex1 < segOfIdx.Length && segOfIdx[w.boneIndex1] >= 0) acc[segOfIdx[w.boneIndex1]] += w.weight1;
                    if (w.boneIndex2 < segOfIdx.Length && segOfIdx[w.boneIndex2] >= 0) acc[segOfIdx[w.boneIndex2]] += w.weight2;
                    if (w.boneIndex3 < segOfIdx.Length && segOfIdx[w.boneIndex3] >= 0) acc[segOfIdx[w.boneIndex3]] += w.weight3;
                    for (int i = 0; i < acc.Length; i++) if (acc[i] >= 0.5f) { pts[i].Add(d.Pos[v]); break; }
                }
            }
            var sbLog = new StringBuilder("PhysicsHands: her colliders from " + bodies + " body meshes:");
            for (int i = 0; i < segs.Count; i++)
            {
                var g = segs[i];
                var list = pts[i];
                g.Verts = list.Count;
                var dists = new List<float>(list.Count);
                if (g.Sphere)
                {
                    Vector3 c = Vector3.zero;
                    foreach (var p in list) c += p;
                    if (list.Count > 0) c /= list.Count;
                    Matrix4x4 bind;
                    if (list.Count >= 8 && bindOfBone.TryGetValue(g.A, out bind))
                    {
                        g.LocalCenter = bind.MultiplyPoint3x4(c);
                        foreach (var p in list) dists.Add((p - c).magnitude);
                    }
                }
                else if (posOf.TryGetValue(g.A, out pa) && posOf.TryGetValue(g.B, out pb))
                {
                    Vector3 ab = pb - pa; float l2 = ab.sqrMagnitude;
                    foreach (var p in list)
                    {
                        float t = l2 > 1e-10f ? Mathf.Clamp01(Vector3.Dot(p - pa, ab) / l2) : 0f;
                        dists.Add((p - (pa + ab * t)).magnitude);
                    }
                }
                if (dists.Count >= 12)
                {
                    dists.Sort();
                    g.RSkin = dists[(int)(0.4f * (dists.Count - 1))] * _herUnit;
                }
                float inset = Inset(g, s.Softness);
                g.RBase = Mathf.Max(0.012f, g.RSkin - inset);
                sbLog.Append("\n  ").Append(g.Name).Append(" (").Append(g.A.name).Append(g.B != null ? "->" + g.B.name : " sphere").Append("): ")
                     .Append(g.Verts).Append(" verts, skin r ").Append(g.RSkin.ToString("0.000")).Append(dists.Count >= 12 ? "" : " (default)")
                     .Append(", inset ").Append(inset.ToString("0.000")).Append(" -> r ").Append(g.RBase.ToString("0.000")).Append(g.Soft ? " [soft zone]" : "");
            }
            Plugin.Log.LogInfo(sbLog.ToString());

            _herGo = new GameObject(Pfx + "_her");
            foreach (var g in segs)
            {
                g.BuildScale = Mathf.Max(1e-4f, Mathf.Abs(g.A.lossyScale.x));
                g.Go = new GameObject(HerPfx + g.Name);
                g.Go.layer = _layerHer;
                g.T = g.Go.transform;
                g.T.SetParent(_herGo.transform, false);
                g.Col = g.Go.AddComponent<CapsuleCollider>();
                g.Col.direction = 1; g.Col.radius = g.RBase; g.Col.height = 2f * g.RBase;
                g.Rb = g.Go.AddComponent<Rigidbody>();
                g.Rb.isKinematic = true; g.Rb.useGravity = false;
                g.Rb.interpolation = RigidbodyInterpolation.None;
                _segs.Add(g);
            }
            _herBuilt = true;
            _herSoftness = s.Softness;
            _herNote = "";
            _statusDirty = true;
            if (_dbgOn) AddHerMarkers();
        }

        static void DestroyHer()
        {
            RemoveHerMarkers();
            foreach (var g in _segs) if (g.Go != null) UnityEngine.Object.Destroy(g.Go);
            _segs.Clear();
            if (_herGo != null) UnityEngine.Object.Destroy(_herGo);
            _herGo = null; _herRoot = null; _herBuilt = false;
            _statusDirty = true;
        }

        // Her live bones -> the kinematic capsules (MovePosition/MoveRotation so a moving hand feels her motion).
        static void StepHer()
        {
            if (!_herBuilt) return;
            for (int i = 0; i < _segs.Count; i++)
            {
                var g = _segs[i];
                if (g.A == null || (!g.Sphere && g.B == null) || g.Go == null) { _herDirty = true; continue; }
                Vector3 p; Quaternion q = Quaternion.identity; float len = 0f;
                if (g.Sphere) p = g.A.TransformPoint(g.LocalCenter);
                else
                {
                    Vector3 a = g.A.position, d = g.B.position - a;
                    len = d.magnitude;
                    if (!(len > 1e-4f) || !Finite(d)) continue;
                    p = a + d * 0.5f;
                    q = Quaternion.FromToRotation(Vector3.up, d / len);
                }
                if (!Finite(p)) continue;
                float ratio = Mathf.Abs(g.A.lossyScale.x) / g.BuildScale;
                if (Mathf.Abs(ratio - g.Ratio) > 0.01f) g.Ratio = ratio;   // her bones were scaled: the skin got fatter or thinner
                float r = g.RBase * g.Ratio;
                float h = Mathf.Max(2f * r, len + 2f * r);
                if (Mathf.Abs(r - g.Col.radius) > 1e-4f || Mathf.Abs(h - g.H) > 1e-3f)
                {
                    g.Col.radius = r; g.Col.height = h; g.H = h;
                    if (g.Mark != null) g.Mark.transform.localScale = new Vector3(2f * r, h * 0.5f, 2f * r);
                }
                if (!g.Placed || (p - g.LastPos).sqrMagnitude > 0.25f)
                {
                    g.T.SetPositionAndRotation(p, q);
                    g.Rb.position = p; g.Rb.rotation = q;
                    g.Placed = true;
                }
                else { g.Rb.MovePosition(p); g.Rb.MoveRotation(q); }
                g.LastPos = p;
            }
        }

        // ================= debug markers =================

        static bool _dbgOn;
        static Material _matHand, _matHer;
        static readonly string[] ColorProps = { "_UnlitColor", "_BaseColor", "_Color" };

        static Material MarkMat(Color c)
        {
            var sh = Shader.Find("HDRP/Unlit");
            if (sh == null) sh = Shader.Find("HDRP/Lit");
            if (sh == null) return null;
            var m = new Material(sh) { name = Pfx + "_debug" };
            for (int i = 0; i < ColorProps.Length; i++) if (m.HasProperty(ColorProps[i])) m.SetColor(ColorProps[i], c);
            return m;
        }

        static GameObject NewMarker(Transform parent, PrimitiveType type, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = Pfx + "_debug";
            foreach (var comp in go.GetComponents<Component>())   // markers must never be touched or touch anything
                if (comp != null && comp.GetType().Name.EndsWith("Collider", StringComparison.Ordinal)) UnityEngine.Object.DestroyImmediate(comp);
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<Renderer>();
            if (r != null)
            {
                r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
                if (mat != null) r.sharedMaterial = mat;
            }
            return go;
        }

        static void AddMarkers(Hand h)
        {
            if (_matHand == null) _matHand = MarkMat(new Color(0.1f, 0.7f, 1f, 1f));
            RemoveMarkers(h);
            float u = h.Spec.Scale;
            for (int i = 0; i < h.Spec.Cols.Count && i < h.ColT.Count; i++)
            {
                var sh = h.Spec.Cols[i];
                GameObject m;
                if (sh.Box) { m = NewMarker(h.ColT[i], PrimitiveType.Cube, _matHand); m.transform.localScale = sh.Size / u; }
                else
                {
                    float len = (sh.B - sh.A).magnitude * u;
                    m = NewMarker(h.ColT[i], PrimitiveType.Capsule, _matHand);
                    m.transform.localScale = new Vector3(2f * sh.R, (len + 2f * sh.R) * 0.5f, 2f * sh.R) / u;
                }
                h.Marks.Add(m);
            }
        }

        static void RemoveMarkers(Hand h)
        {
            foreach (var m in h.Marks) if (m != null) UnityEngine.Object.Destroy(m);
            h.Marks.Clear();
        }

        static void AddHerMarkers()
        {
            if (_matHer == null) _matHer = MarkMat(new Color(1f, 0.5f, 0.1f, 1f));
            foreach (var g in _segs)
            {
                if (g.Go == null || g.Mark != null) continue;
                g.Mark = NewMarker(g.T, PrimitiveType.Capsule, _matHer);
                float r = g.Col.radius, h = Mathf.Max(2f * r, g.Col.height);
                g.Mark.transform.localScale = new Vector3(2f * r, h * 0.5f, 2f * r);
            }
        }

        static void RemoveHerMarkers()
        {
            foreach (var g in _segs) if (g.Mark != null) { UnityEngine.Object.Destroy(g.Mark); g.Mark = null; }
        }

        static void SetDebug(bool on)
        {
            if (on == _dbgOn) return;
            _dbgOn = on;
            if (on)
            {
                if (_left != null) AddMarkers(_left);
                if (_right != null) AddMarkers(_right);
                AddHerMarkers();
            }
            else
            {
                if (_left != null) RemoveMarkers(_left);
                if (_right != null) RemoveMarkers(_right);
                RemoveHerMarkers();
                if (_matHand != null) UnityEngine.Object.Destroy(_matHand);
                if (_matHer != null) UnityEngine.Object.Destroy(_matHer);
                _matHand = _matHer = null;
            }
        }

        // ================= contacts and teleports (log) =================

        static readonly HashSet<string> _contacts = new HashSet<string>();
        static int _enters, _tpFar, _tpJump, _tpNan;
        static float _nextSummary;

        internal static void NoteContact(bool left, Collision c)
        {
            try
            {
                _enters++;
                var col = c.collider;
                string n = col != null ? col.name : "?";
                if (n.StartsWith(HerPfx, StringComparison.Ordinal)) n = n.Substring(HerPfx.Length);
                if (_contacts.Add((left ? "L:" : "R:") + n))
                    Plugin.Log.LogInfo("PhysicsHands: first contact " + (left ? "left" : "right") + " hand with her " + n + " (" + c.relativeVelocity.magnitude.ToString("0.00") + " m/s, " + c.contactCount + " points)");
            }
            catch (Exception) { }
        }

        static void Teleport(Hand h, Vector3 p, Quaternion q)
        {
            var rb = h.Rb;
            if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            rb.position = p; rb.rotation = q;
            h.PRoot.SetPositionAndRotation(p, q);
            h.FarTime = 0f;
        }

        // ================= per fixed step =================

        // Runner.FixedUpdate: the palm follows the target by velocity; her capsules follow her bones. Before the physics step.
        public static void FixedStep()
        {
            if (!Active && !_herBuilt) return;
            try
            {
                float dt = Time.fixedDeltaTime;
                if (_s != null && dt > 0f)
                {
                    if (_left != null) StepHand(_left, VrHands.LeftController, _s.LeftPos, _s.LeftRot, dt);
                    if (_right != null) StepHand(_right, VrHands.RightController, _s.RightPos, _s.RightRot, dt);
                }
                StepHer();
                float now = Time.unscaledTime;
                if (now >= _nextSummary)
                {
                    _nextSummary = now + 10f;
                    if (_tpFar + _tpJump + _tpNan > 0 || _enters > 0)
                        Plugin.Log.LogInfo("PhysicsHands: last 10 s: " + _enters + " contact starts, teleports " + (_tpFar + _tpJump + _tpNan) + " (too far " + _tpFar + ", target jumped " + _tpJump + ", NaN " + _tpNan + ")");
                    _enters = _tpFar = _tpJump = _tpNan = 0;
                }
            }
            catch (Exception e)
            {
                LogOnce("fixederr" + e.GetType().Name, "FixedStep error: " + e);
            }
        }

        static void StepHand(Hand h, Transform ctl, Vector3 off, Vector3 rot, float dt)
        {
            var rb = h.Rb;
            if (h.PGo == null || rb == null) return;
            if (ctl == null)
            {
                h.HaveFixed = false; h.HaveLast = false;
                if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                return;
            }
            Vector3 tp; Quaternion tq;
            Target(ctl, off, rot, h.Spec.Align, out tp, out tq);
            if (!Finite(tp) || !Finite(tq))
            {
                _tpNan++;
                h.HaveFixed = false;
                if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                return;
            }
            h.FixedPos = tp; h.FixedRot = tq; h.HaveFixed = true;
            if (h.Kin)
            {
                rb.MovePosition(tp); rb.MoveRotation(tq);
                h.LastTarget = tp; h.HaveLast = true;
                return;
            }
            Vector3 pos = rb.position; Quaternion rq = rb.rotation;
            if (!Finite(pos) || !Finite(rq)) { _tpNan++; Teleport(h, tp, tq); h.LastTarget = tp; h.HaveLast = true; return; }
            if (h.HaveLast && (tp - h.LastTarget).sqrMagnitude > 1f) { _tpJump++; Teleport(h, tp, tq); h.LastTarget = tp; return; }
            h.LastTarget = tp; h.HaveLast = true;
            Vector3 gap = tp - pos;
            if (gap.sqrMagnitude > 0.35f * 0.35f)
            {
                h.FarTime += dt;
                if (h.FarTime > 0.25f) { _tpFar++; Teleport(h, tp, tq); return; }
            }
            else h.FarTime = 0f;
            Vector3 v = gap / dt * 0.9f;
            if (v.sqrMagnitude > 36f) v = v.normalized * 6f;
            Quaternion dq = tq * Quaternion.Inverse(rq);
            if (dq.w < 0f) dq = new Quaternion(-dq.x, -dq.y, -dq.z, -dq.w);
            float ang; Vector3 axis;
            dq.ToAngleAxis(out ang, out axis);
            if (ang > 180f) ang -= 360f;
            Vector3 w = Finite(axis) ? axis * (ang * Mathf.Deg2Rad) / dt * 0.9f : Vector3.zero;
            if (w.sqrMagnitude > 900f) w = w.normalized * 30f;
            rb.velocity = v; rb.angularVelocity = w;
        }

        // ================= per frame (the apply step) =================

        static bool _statusDirty;

        static void SetDisp(bool left, Vector3 dp, Quaternion dq)
        {
            if (left) { LeftDp = dp; LeftDq = dq; } else { RightDp = dp; RightDq = dq; }
        }

        public static void Update(Character her, HandsSettings s)
        {
            SetDisp(true, Vector3.zero, Quaternion.identity);    // set again by UpdateHand below when the hand is dynamic
            SetDisp(false, Vector3.zero, Quaternion.identity);
            if (s == null || !s.On)
            {
                if (_taken) Release();
                else if (Active || Status.Length > 0) { Active = false; Status = ""; }
                return;
            }
            try { Step(her, s); }
            catch (Exception e)
            {
                Plugin.Log.LogError("PhysicsHands failed, off for 5 s: " + e);
                Release();
                _retryAt = Time.unscaledTime + 5f;
                SetStatus("off: error " + e.GetType().Name + ": " + e.Message);
            }
        }

        static void Step(Character her, HandsSettings s)
        {
            _s = s;
            float now = Time.unscaledTime, dt = Time.deltaTime;
            if (now < _retryAt) return;
            if (!_layersApplied)
            {
                string why = Prepare();
                if (why != null) { SetStatus("off: " + why); _retryAt = now + 5f; return; }
            }
            _taken = true;

            var cl = VrHands.LeftController; var cr = VrHands.RightController;
            if (cl == null) cl = null;   // a destroyed controller reads as null: make it really null
            if (cr == null) cr = null;
            SetDebug(s.ShowColliders);

            // hands: built when their controller is known, rebuilt when the mesh choice changed or a part was destroyed
            ManageHand(ref _left, true, cl, s, now);
            ManageHand(ref _right, false, cr, s, now);
            if (_left == null && _right == null)
            {
                if (cl == null && cr == null) SetStatus("off: no VR controller found (VrHands has none yet)");
                else if (_statusDirty) SetStatus("off: couldn't build the hands (see the log)");
                _statusDirty = false;
                Active = false;
                if (_herBuilt) DestroyHer();
                return;
            }

            // her body
            if (s.Collide && her != null && her.Root != null)
            {
                if (!_herBuilt && now >= _herNext || (_herBuilt && (_herRoot != her.Root || _herDirty)))
                {
                    try { BuildHer(her, s); }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError("PhysicsHands: her colliders failed: " + e);
                        DestroyHer();
                        _herNote = "her: collider build failed";
                        _herNext = now + 10f;
                    }
                }
                if (_herBuilt && Mathf.Abs(s.Softness - _herSoftness) > 0.02f)
                {
                    _herSoftness = s.Softness;   // the soft zones' inset follows the softness setting
                    foreach (var g in _segs) g.RBase = Mathf.Max(0.012f, g.RSkin - Inset(g, s.Softness));
                }
            }
            else if (_herBuilt) DestroyHer();

            // hands every frame
            if (_left != null) UpdateHand(_left, cl, s, dt, now);
            if (_right != null) UpdateHand(_right, cr, s, dt, now);
            if (_left != null && cl != null) HideController(_hideL, true, now);
            if (_right != null && cr != null) HideController(_hideR, false, now);
            if (_left == null) RestoreHide(_hideL);
            if (_right == null) RestoreHide(_hideR);

            if (_statusDirty)
            {
                _statusDirty = false;
                SetStatus(OnStatus(s, her));
            }
        }

        static string OnStatus(HandsSettings s, Character her)
        {
            var sb = new StringBuilder("on: ");
            AppendHand(sb, _left, "L"); sb.Append(", "); AppendHand(sb, _right, "R");
            if (!s.Collide) sb.Append("; collisions off (visual only)");
            else if (_herBuilt) sb.Append("; her ").Append(_segs.Count).Append(" colliders");
            else sb.Append("; ").Append(_herNote.Length > 0 ? _herNote : (her == null ? "her: not found" : "her: building"));
            sb.Append("; layers ").Append(_layerHand).Append('/').Append(_layerHer);
            return sb.ToString();
        }

        static void AppendHand(StringBuilder sb, Hand h, string side)
        {
            if (h == null) { sb.Append(side).Append(" none"); return; }
            sb.Append(side).Append(' ').Append(h.Spec.Visual).Append(h.Fallback ? " (fallback)" : "").Append(", grip ").Append(h.GripDesc);
        }

        static void ManageHand(ref Hand slot, bool left, Transform ctl, HandsSettings s, float now)
        {
            var h = slot;
            if (h != null && (h.PGo == null || h.VGo == null || h.BuiltMesh != s.Mesh))
            {
                DestroyHand(h); RestoreHide(left ? _hideL : _hideR);
                slot = h = null;
                RebuildCapsNow = true;
            }
            if (h == null)
            {
                if (ctl == null || now < (left ? _nextBuildL : _nextBuildR)) { if (RebuildCapsNow) { RebuildCaps(); RebuildCapsNow = false; } return; }
                slot = MakeHand(left, ctl, s, null);
                if (slot == null) { if (left) _nextBuildL = now + 5f; else _nextBuildR = now + 5f; }
                RebuildCapsNow = true;
            }
            else if (h.Fallback && s.Mesh == 0 && now >= h.NextUpgrade && ctl != null)
            {
                // the player wasn't there (or its hand couldn't be read) at first: look again now and then
                h.NextUpgrade = now + 10f;
                string why;
                HandSpec sp = null;
                try { sp = GameSpec(left, out why); } catch (Exception) { }
                if (sp != null)
                {
                    DestroyHand(h); RestoreHide(left ? _hideL : _hideR);
                    slot = MakeHand(left, ctl, s, sp);
                    RebuildCapsNow = true;
                }
            }
            if (RebuildCapsNow) { RebuildCaps(); RebuildCapsNow = false; }
        }
        static bool RebuildCapsNow;
        static float _nextBuildL, _nextBuildR;

        static void SetVisible(Hand h, bool on)
        {
            for (int i = 0; i < h.Rends.Count; i++)
            {
                var r = h.Rends[i];
                if (r != null && r.enabled != on) r.enabled = on;
            }
        }

        static void SetCollide(Hand h, bool on, Vector3 p, Quaternion q)
        {
            h.CollideOn = on;
            foreach (var c in h.Cols) if (c != null) c.enabled = on;
            var rb = h.Rb;
            if (on)
            {
                rb.isKinematic = false;
                Teleport(h, p, q);
                h.Kin = false;
            }
            else
            {
                rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
                h.Kin = true;
            }
        }

        static void UpdateHand(Hand h, Transform ctl, HandsSettings s, float dt, float now)
        {
            if (h.PGo == null || h.VGo == null) return;
            if (ctl == null) { SetVisible(h, false); return; }
            Vector3 tp; Quaternion tq;
            Target(ctl, h.Left ? s.LeftPos : s.RightPos, h.Left ? s.LeftRot : s.RightRot, h.Spec.Align, out tp, out tq);
            if (!Finite(tp) || !Finite(tq)) return;
            if (h.CollideOn != s.Collide) SetCollide(h, s.Collide, tp, tq);

            // The visual: the fresh controller target composed with where the body stands relative to the target it chased at the
            // last fixed step. In free air that offset is ~0 (1:1 with the controller, no added latency); blocked, it is the displacement.
            Vector3 vp = tp; Quaternion vq = tq;
            if (s.Collide && h.HaveFixed && !h.Kin)
            {
                Vector3 dp = h.Rb.position - h.FixedPos;
                Quaternion dq = h.Rb.rotation * Quaternion.Inverse(h.FixedRot);
                if (Finite(dp) && Finite(dq) && dp.sqrMagnitude < 0.5f * 0.5f) { vp += dp; vq = dq * tq; SetDisp(h.Left, dp, dq); }
            }
            h.VRoot.SetPositionAndRotation(vp, vq);

            // grip -> curl
            float g = ReadGrip(h, now);
            h.Curl = Mathf.Lerp(h.Curl, g, 1f - Mathf.Exp(-25f * dt));
            if (Mathf.Abs(h.Curl - h.AppliedCurl) > 0.002f) { h.AppliedCurl = h.Curl; PoseFingers(h); }

            SetVisible(h, !s.HideVisual);

            // materials of a hand skin the game unloaded: swap in ours, keep the mesh
            if (now >= h.NextMat)
            {
                h.NextMat = now + 2f;
                for (int i = 0; i < h.Rends.Count; i++)
                {
                    var smr = h.Rends[i] as SkinnedMeshRenderer;
                    if (smr == null) continue;
                    var mats = smr.sharedMaterials;
                    bool fix = false;
                    for (int k = 0; k < mats.Length; k++) if (mats[k] == null) { mats[k] = SkinMat(); fix = true; }
                    if (fix) { smr.sharedMaterials = mats; LogOnce("mat" + h.Left, "a hand material was destroyed, using the fallback skin for it"); }
                }
            }
        }

        static void PoseFingers(Hand h)
        {
            float c = h.Curl;
            for (int f = 0; f < 5; f++)
                for (int j = 0; j < 3; j++)
                {
                    int i = f * 3 + j, bi = h.Spec.Fin[i];
                    if (bi < 0) continue;
                    float max = f == 0 ? ThumbMax[j] : FingerMax[j];
                    var q = h.Rest[i] * Quaternion.AngleAxis(max * c, h.AxisLocal[i]);
                    h.VBones[bi].localRotation = q;
                    h.PBones[bi].localRotation = q;
                }
        }

        // ================= release =================

        public static void Release()
        {
            try
            {
                DestroyHand(_left); DestroyHand(_right);
                _left = _right = null;
                DestroyHer();
                RestoreHide(_hideL); RestoreHide(_hideR);
                if (_dbgOn) SetDebug(false);
                RestoreLayers();
                if (_skinMat != null) UnityEngine.Object.Destroy(_skinMat);
                _skinMat = null;
                bool had = Capsules.Count > 0;
                Capsules.Clear();
                if (had) Version++;
            }
            catch (Exception e) { Plugin.Log.LogWarning("PhysicsHands: release: " + e.Message); }
            LeftWrist = RightWrist = null;
            SetDisp(true, Vector3.zero, Quaternion.identity); SetDisp(false, Vector3.zero, Quaternion.identity);
            Active = false;
            _taken = false; _retryAt = 0f; _nextBuildL = _nextBuildR = 0f; _herNext = 0f; _herNote = "";
            _herSoftness = -1f; _statusDirty = false; RebuildCapsNow = false;
            _contacts.Clear();
            _enters = _tpFar = _tpJump = _tpNan = 0;
            Status = "";
        }
    }
}
