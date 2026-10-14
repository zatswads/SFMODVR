using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // A slider VaM-style, done with bones (the game's meshes have no blendshapes):
    //   Size    uniform scale; Keep = scale the next joint back, so only this part changes
    //           (uniform scale commutes with rotation, so it never skews the bones below);
    //   Stretch scale along one body direction; only on leaf bones, or with Keep where the
    //           bones below barely rotate (spine), since a non-uniform scale skews rotating children;
    //   Move    shift along a body direction, in cm (translation never skews);
    //   Length  push the next joint along the bone (thigh, arm, neck length).
    internal enum Op { Size, Stretch, Move, Length }
    internal enum Dir { None, Up, Out, Forward, Back, Width, Depth }

    internal class Detail
    {
        public string Id, Group, Label;
        public Op Op;
        public Dir Dir;
        public string Part;              // GameRig part id (the game's own bone references), preferred
        public string[] Names;           // exact bone names of this rig, next
        public string[] Words, Not;      // name keywords (other rigs), last
        public float Min, Max;
        public bool Keep;
        public float Neutral { get { return Op == Op.Move ? 0f : 1f; } }
        public string Unit { get { return Op == Op.Move ? " cm" : ""; } }

        // "Left_Nipple" -> both sides.
        static string[] Pair(string left) { return new[] { left, "Right_" + left.Substring("Left_".Length) }; }

        public static readonly string[] Groups = { "Breasts", "Glutes", "Hips & legs", "Torso", "Arms", "Head & face" };

        // Ids of the 0.1/0.2 regions are kept, so saved looks still load.
        public static readonly Detail[] All =
        {
            new Detail { Id = "breasts", Group = "Breasts", Label = "Size", Op = Op.Size, Part = "breasts", Words = new[] { "breast", "boob", "bust", "pectoral", "mune", "oppai" }, Not = new[] { "nipple" }, Min = 0.3f, Max = 2.5f },
            new Detail { Id = "breastLift", Group = "Breasts", Label = "Lift", Op = Op.Move, Dir = Dir.Up, Part = "breasts", Min = -4f, Max = 4f },
            new Detail { Id = "breastSpacing", Group = "Breasts", Label = "Spacing", Op = Op.Move, Dir = Dir.Out, Part = "breasts", Min = -3f, Max = 3f },
            new Detail { Id = "breastProjection", Group = "Breasts", Label = "Projection", Op = Op.Move, Dir = Dir.Forward, Names = Pair("Left_LowerBreast"), Min = -2f, Max = 4f },
            new Detail { Id = "breastLower", Group = "Breasts", Label = "Lower fullness", Op = Op.Size, Names = Pair("Left_LowerBreast"), Min = 0.5f, Max = 2f },
            new Detail { Id = "breastTop", Group = "Breasts", Label = "Top", Op = Op.Size, Names = Pair("Left_Breast_up"), Min = 0.5f, Max = 2f },
            new Detail { Id = "breastBottom", Group = "Breasts", Label = "Bottom", Op = Op.Size, Names = Pair("Left_Breast_down"), Min = 0.5f, Max = 2f },
            new Detail { Id = "breastInner", Group = "Breasts", Label = "Inner side", Op = Op.Size, Names = Pair("Left_Breast_in"), Min = 0.5f, Max = 2f },
            new Detail { Id = "breastOuter", Group = "Breasts", Label = "Outer side", Op = Op.Size, Names = Pair("Left_Breast_out"), Min = 0.5f, Max = 2f },
            new Detail { Id = "nipples", Group = "Breasts", Label = "Nipple size", Op = Op.Size, Names = Pair("Left_Nipple"), Words = new[] { "nipple", "areola" }, Min = 0.3f, Max = 3f },
            new Detail { Id = "nippleProjection", Group = "Breasts", Label = "Nipple projection", Op = Op.Stretch, Dir = Dir.Depth, Names = Pair("Left_Nipple"), Min = 0.5f, Max = 2.5f },

            new Detail { Id = "glutes", Group = "Glutes", Label = "Size", Op = Op.Size, Names = new[] { "Butt_L", "Butt_R" }, Words = new[] { "glute", "buttock", "butt", "siri" }, Min = 0.5f, Max = 2f },
            new Detail { Id = "gluteLift", Group = "Glutes", Label = "Lift", Op = Op.Move, Dir = Dir.Up, Names = new[] { "Butt_L", "Butt_R" }, Min = -3f, Max = 3f },
            new Detail { Id = "gluteProjection", Group = "Glutes", Label = "Projection", Op = Op.Move, Dir = Dir.Back, Names = new[] { "Butt_L", "Butt_R" }, Min = -2f, Max = 4f },
            new Detail { Id = "gluteSpacing", Group = "Glutes", Label = "Spacing", Op = Op.Move, Dir = Dir.Out, Names = new[] { "Butt_L", "Butt_R" }, Min = -2f, Max = 3f },
            new Detail { Id = "gluteSkin", Group = "Glutes", Label = "Fullness", Op = Op.Size, Names = new[] { "ButtSkin_L", "ButtSkin_R" }, Min = 0.5f, Max = 2f },

            new Detail { Id = "hips", Group = "Hips & legs", Label = "Pelvis size", Op = Op.Size, Part = "hips", Words = new[] { "pelvis", "hips" }, Min = 0.7f, Max = 1.5f, Keep = true },
            new Detail { Id = "hipWidth", Group = "Hips & legs", Label = "Hip width", Op = Op.Move, Dir = Dir.Out, Part = "thighs", Min = -2f, Max = 4f },
            new Detail { Id = "thighs", Group = "Hips & legs", Label = "Thigh size", Op = Op.Size, Part = "thighs", Words = new[] { "thigh", "upperleg", "upleg" }, Min = 0.6f, Max = 1.6f, Keep = true },
            new Detail { Id = "thighFront", Group = "Hips & legs", Label = "Thigh front", Op = Op.Size, Names = new[] { "LegFrontSkin_L", "LegFrontSkin_R" }, Min = 0.5f, Max = 2f },
            new Detail { Id = "thighInner", Group = "Hips & legs", Label = "Inner thigh", Op = Op.Size, Names = new[] { "LegInsiteSkin_L", "LegInsiteSkin_R" }, Min = 0.5f, Max = 2f },
            new Detail { Id = "thighOuter", Group = "Hips & legs", Label = "Outer thigh", Op = Op.Size, Names = new[] { "LegOutsiteSkin_L", "LegOutsiteSkin_R" }, Min = 0.5f, Max = 2f },
            new Detail { Id = "thighLength", Group = "Hips & legs", Label = "Thigh length", Op = Op.Length, Part = "thighs", Min = 0.85f, Max = 1.2f },
            new Detail { Id = "calves", Group = "Hips & legs", Label = "Calf size", Op = Op.Size, Part = "calves", Words = new[] { "calf", "shin", "lowerleg" }, Min = 0.6f, Max = 1.6f, Keep = true },
            new Detail { Id = "calfLength", Group = "Hips & legs", Label = "Calf length", Op = Op.Length, Part = "calves", Min = 0.85f, Max = 1.2f },
            new Detail { Id = "feet", Group = "Hips & legs", Label = "Feet", Op = Op.Size, Part = "feet", Words = new[] { "foot" }, Not = new[] { "ik" }, Min = 0.7f, Max = 1.4f },

            new Detail { Id = "waist", Group = "Torso", Label = "Waist size", Op = Op.Size, Part = "waist", Words = new[] { "waist", "abdomen", "belly", "stomach" }, Min = 0.6f, Max = 1.5f, Keep = true },
            new Detail { Id = "waistWidth", Group = "Torso", Label = "Waist width", Op = Op.Stretch, Dir = Dir.Width, Part = "waist", Min = 0.7f, Max = 1.4f, Keep = true },
            new Detail { Id = "waistDepth", Group = "Torso", Label = "Waist depth", Op = Op.Stretch, Dir = Dir.Depth, Part = "waist", Min = 0.7f, Max = 1.4f, Keep = true },
            new Detail { Id = "belly", Group = "Torso", Label = "Belly", Op = Op.Size, Names = new[] { "Uterus_belly" }, Min = 0.5f, Max = 2.5f },
            new Detail { Id = "ribcage", Group = "Torso", Label = "Ribcage size", Op = Op.Size, Part = "ribcage", Words = new[] { "chest", "ribcage" }, Not = new[] { "breast" }, Min = 0.7f, Max = 1.4f, Keep = true },
            new Detail { Id = "ribcageWidth", Group = "Torso", Label = "Ribcage width", Op = Op.Stretch, Dir = Dir.Width, Part = "ribcage", Min = 0.8f, Max = 1.3f, Keep = true },
            new Detail { Id = "ribcageDepth", Group = "Torso", Label = "Ribcage depth", Op = Op.Stretch, Dir = Dir.Depth, Part = "ribcage", Min = 0.8f, Max = 1.3f, Keep = true },
            new Detail { Id = "torsoLength", Group = "Torso", Label = "Torso length", Op = Op.Length, Part = "waist", Min = 0.85f, Max = 1.2f },
            new Detail { Id = "shoulderWidth", Group = "Torso", Label = "Shoulder width", Op = Op.Move, Dir = Dir.Out, Names = new[] { "Scapula_L", "Scapula_R" }, Min = -3f, Max = 3f },

            new Detail { Id = "upperarms", Group = "Arms", Label = "Upper arm size", Op = Op.Size, Part = "upperarms", Words = new[] { "upperarm", "uparm", "shldr" }, Not = new[] { "twist" }, Min = 0.6f, Max = 1.6f, Keep = true },
            new Detail { Id = "armLength", Group = "Arms", Label = "Upper arm length", Op = Op.Length, Part = "upperarms", Min = 0.85f, Max = 1.2f },
            new Detail { Id = "forearms", Group = "Arms", Label = "Forearm size", Op = Op.Size, Part = "forearms", Words = new[] { "forearm", "lowerarm" }, Min = 0.6f, Max = 1.6f, Keep = true },
            new Detail { Id = "forearmLength", Group = "Arms", Label = "Forearm length", Op = Op.Length, Part = "forearms", Min = 0.85f, Max = 1.2f },
            new Detail { Id = "hands", Group = "Arms", Label = "Hands", Op = Op.Size, Part = "hands", Words = new[] { "hand" }, Not = new[] { "handle", "ik" }, Min = 0.7f, Max = 1.4f },

            new Detail { Id = "head", Group = "Head & face", Label = "Head size", Op = Op.Size, Part = "head", Words = new[] { "head" }, Not = new[] { "headtop", "headend", "end" }, Min = 0.8f, Max = 1.25f },
            new Detail { Id = "neck", Group = "Head & face", Label = "Neck size", Op = Op.Size, Part = "neck", Words = new[] { "neck" }, Min = 0.7f, Max = 1.5f, Keep = true },
            new Detail { Id = "neckLength", Group = "Head & face", Label = "Neck length", Op = Op.Length, Part = "neck", Min = 0.8f, Max = 1.3f },
            new Detail { Id = "eyes", Group = "Head & face", Label = "Eye size", Op = Op.Size, Names = new[] { "Eye_L", "Eye_R" }, Min = 0.85f, Max = 1.2f },
            new Detail { Id = "cheeks", Group = "Head & face", Label = "Cheeks", Op = Op.Size, Names = new[] { "Cheek_Left", "Cheek_Right" }, Min = 0.6f, Max = 1.6f },
            new Detail { Id = "cheekHeight", Group = "Head & face", Label = "Cheekbone height", Op = Op.Move, Dir = Dir.Up, Names = new[] { "Cheek_Left_Raiser", "Cheek_Right_Raiser" }, Min = -0.6f, Max = 0.6f },
            new Detail { Id = "brows", Group = "Head & face", Label = "Brow height", Op = Op.Move, Dir = Dir.Up, Names = new[] { "Eyebrow_Left_Insite", "Eyebrow_Left_Middle", "Eyebrow_Left_Outsite", "Eyebrow_Right_Insite", "Eyebrow_Right_Middle", "Eyebrow_Right_Outsite" }, Min = -0.5f, Max = 0.5f },
            new Detail { Id = "lips", Group = "Head & face", Label = "Lips", Op = Op.Size, Names = new[] { "Lip_Left_Inside", "Lip_LeftLower_Insite", "Lip_LeftUpper_Inside", "Lip_Lower_Insite", "Lip_Right_Inside", "Lip_RightLower_Insite", "Lip_RightUpper_Inside", "Lip_Upper_Inside" }, Min = 0.7f, Max = 1.5f },
            new Detail { Id = "jaw", Group = "Head & face", Label = "Jaw", Op = Op.Size, Names = new[] { "Jaw_M" }, Min = 0.8f, Max = 1.25f, Keep = true },
        };

        public bool Matches(string norm)
        {
            if (Words == null) return false;
            if (Not != null) foreach (var w in Not) if (norm.Contains(w)) return false;
            foreach (var w in Words) if (norm.Contains(w)) return true;
            return false;
        }
    }

    internal class Character
    {
        public Transform Root;
        public string Key;
        public bool Female, IsPlayer;
        public readonly List<SkinnedMeshRenderer> Renderers = new List<SkinnedMeshRenderer>();
        // Skin bones in hierarchy order (what the Bones tab lists), by name for lookups.
        public readonly List<Transform> BoneList = new List<Transform>();
        public readonly Dictionary<string, Transform> Bones = new Dictionary<string, Transform>();
        public readonly Dictionary<string, List<Transform>> DetailBones = new Dictionary<string, List<Transform>>();
        public readonly Dictionary<string, string> Source = new Dictionary<string, string>();  // detail id -> how its bones were found
        public readonly Dictionary<string, string> Off = new Dictionary<string, string>();     // detail id -> why it can't apply
        public readonly List<Transform> Heads = new List<Transform>();
        // Pose-independent directions from the bind pose: per bone, the local axis (0..2) of
        // Width/Depth/Up, and per moved bone the local offset of 1 m Up/Out/Forward in its parent.
        public readonly Dictionary<Transform, int[]> Axis = new Dictionary<Transform, int[]>();
        public readonly Dictionary<Transform, Vector3[]> MoveDir = new Dictionary<Transform, Vector3[]>();
        public readonly Look Look = new Look();
        public bool Dirty;
        public float DirtyAt;
    }

    // Finds the characters (skinned meshes grouped by root, the game's woman by
    // MovePlayerAndGirl.reika), keeps their looks, and applies them every frame
    // right after the game's animation and before its physics and IK.
    internal class Body
    {
        readonly Plugin _p;
        public readonly List<Character> Found = new List<Character>();
        public string Status = "looking for characters";
        float _nextScan;
        int _frame;
        readonly HashSet<string> _logged = new HashSet<string>();
        Transform _lady;

        // What we wrote, so we can tell the game's own changes from ours and undo ours.
        class ScaleTrack { public Transform T; public Vector3 Rest, Last; public int Used; }
        class PosTrack { public Transform T; public Vector3 Rest, Last; public int Used; }
        readonly Dictionary<Transform, ScaleTrack> _scales = new Dictionary<Transform, ScaleTrack>();
        readonly Dictionary<Transform, PosTrack> _poss = new Dictionary<Transform, PosTrack>();
        readonly Dictionary<Transform, Vector3> _net = new Dictionary<Transform, Vector3>();     // scale factor
        readonly Dictionary<Transform, float> _len = new Dictionary<Transform, float>();         // localPosition factor
        readonly Dictionary<Transform, Vector3> _add = new Dictionary<Transform, Vector3>();     // localPosition offset
        readonly List<Transform> _drop = new List<Transform>();
        readonly HashSet<Transform> _posTargets = new HashSet<Transform>();

        public Body(Plugin p) { _p = p; }

        public static string Norm(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // "Hip_L" -> "hip", "Left_UpperBreast" -> "upperbreast": a child whose name starts with
        // its parent's stem (HipPart1_L, NeckPart1_M) is a helper of the same part, not the next joint.
        static string Stem(string name)
        {
            var n = name;
            if (n.StartsWith("Left_") || n.StartsWith("Right_")) n = n.Substring(n.IndexOf('_') + 1);
            if (n.EndsWith("_L") || n.EndsWith("_R") || n.EndsWith("_M")) n = n.Substring(0, n.Length - 2);
            return Norm(n);
        }

        // ---- scanning (every 2 s, cached per character) ----

        public void Tick()
        {
            if (Time.unscaledTime >= _nextScan) { _nextScan = Time.unscaledTime + 2f; Scan(); }
            foreach (var c in Found)
                if (c.Dirty && Time.unscaledTime - c.DirtyAt > 1f)
                {
                    c.Dirty = false;
                    try { Store.SaveCharacter(c.Key, c.Look); }
                    catch (Exception e) { Plugin.Log.LogWarning("Couldn't save " + c.Key + ": " + e.Message); }
                }
        }

        void Scan()
        {
            var byRoot = new Dictionary<Transform, List<SkinnedMeshRenderer>>();
            var order = new List<Transform>();
            // The game's woman is everything under MovePlayerAndGirl.reika (whatever her scene root holds besides).
            _lady = GameRig.Lady();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>())
            {
                if (r == null || r.sharedMesh == null) continue;
                var root = _lady != null && r.transform.IsChildOf(_lady) ? _lady : r.transform.root;
                List<SkinnedMeshRenderer> list;
                if (!byRoot.TryGetValue(root, out list)) { list = new List<SkinnedMeshRenderer>(); byRoot[root] = list; order.Add(root); }
                list.Add(r);
            }
            foreach (var l in byRoot.Values) l.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID())); // stable order for Same()
            var old = new Dictionary<Transform, Character>();
            foreach (var c in Found) if (c.Root != null) old[c.Root] = c;
                else if (c.Dirty) Store.SaveCharacter(c.Key, c.Look); // character gone: keep unsaved edits
            Found.Clear();
            // Keys (file names) must be unique: two copies of one character are "Name" and "Name 2".
            var used = new HashSet<string>();
            foreach (var root in order) { Character c; if (old.TryGetValue(root, out c) && Same(c.Renderers, byRoot[root])) used.Add(c.Key); }
            foreach (var root in order)
            {
                var list = byRoot[root];
                Character c;
                if (!old.TryGetValue(root, out c) || !Same(c.Renderers, list)) c = Build(root, list, c, used);
                Found.Add(c);
            }
            var eyes = new List<Vector3>();
            foreach (var cam in Camera.allCameras) if (cam.stereoEnabled || cam == Camera.main) eyes.Add(cam.transform.position);
            int women = 0;
            foreach (var c in Found)
            {
                c.IsPlayer = false;
                foreach (var h in c.Heads)
                    foreach (var e in eyes)
                        if (h != null && (h.position - e).sqrMagnitude < 0.2f * 0.2f) c.IsPlayer = true;
                if (c.Female && !c.IsPlayer) women++;
            }
            Status = _lady != null && women > 0 ? "the game's woman found"
                   : women > 0 ? women + (women == 1 ? " woman" : " women") + " found"
                   : Found.Count > 0 ? "couldn't tell who's female: showing everyone" : "no characters in the scene";
        }

        static bool Same(List<SkinnedMeshRenderer> a, List<SkinnedMeshRenderer> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        Character Build(Transform root, List<SkinnedMeshRenderer> list, Character prev, HashSet<string> used)
        {
            string key = prev != null ? prev.Key : root.name.Replace("(Clone)", "").Trim();
            if (key.Length == 0) key = "Character";
            if (prev == null && used.Contains(key)) { int n = 2; while (used.Contains(key + " " + n)) n++; key = key + " " + n; }
            used.Add(key);
            var c = new Character { Root = root, Key = key };
            c.Renderers.AddRange(list);
            if (prev != null) { c.Look.CopyFrom(prev.Look); c.Dirty = prev.Dirty; c.DirtyAt = prev.DirtyAt; } // clothes changed: same person
            else Store.LoadCharacter(c.Key, c.Look);

            // Skin bones and their bind poses (mesh space -> bone), from the first mesh that has each bone.
            var bind = new Dictionary<Transform, Matrix4x4>();
            foreach (var r in list)
            {
                var bones = r.bones;
                var bp = r.sharedMesh.bindposes;
                for (int i = 0; i < bones.Length; i++)
                    if (bones[i] != null && !bind.ContainsKey(bones[i]) && i < bp.Length) bind[bones[i]] = bp[i];
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) // hierarchy order: parents first
                if (bind.ContainsKey(t))
                {
                    c.BoneList.Add(t);
                    if (!c.Bones.ContainsKey(t.name)) c.Bones[t.name] = t;
                    if (Norm(t.name).Contains("head")) c.Heads.Add(t);
                }

            var rig = root == _lady ? GameRig.Parts(_lady) : new Dictionary<string, List<Transform>>();
            foreach (var d in Detail.All)
            {
                List<Transform> hits;
                if (d.Part != null && rig.TryGetValue(d.Part, out hits)) c.Source[d.Id] = "the game's own bone";
                else
                {
                    hits = new List<Transform>();
                    if (d.Names != null) foreach (var n in d.Names) { Transform t; if (c.Bones.TryGetValue(n, out t)) hits.Add(t); }
                    if (hits.Count > 0) c.Source[d.Id] = "this rig's bone";
                    else if (d.Words != null)
                    {
                        foreach (var t in c.BoneList)
                        {
                            if (!d.Matches(Norm(t.name))) continue;
                            // Only the top of a matching chain (Breast_L, not also Breast_L_02 under it).
                            bool under = false;
                            for (var a = t.parent; a != null && !under; a = a.parent) under = hits.Contains(a);
                            if (!under) hits.Add(t);
                        }
                        if (hits.Count > 0) c.Source[d.Id] = "found by name";
                    }
                }
                c.DetailBones[d.Id] = hits;
                if (hits.Count == 0) c.Off[d.Id] = "this rig has no bone for it";
                else if (d.Op == Op.Stretch && !d.Keep)
                    foreach (var t in hits) if (t.childCount > 0) { c.Off[d.Id] = "would skew the bones under " + t.name; break; }
            }

            Directions(c, bind, rig);

            if (_lady != null) c.Female = root == _lady; // the game has exactly one woman
            else
            {
                var female = new[] { "lady", "female", "girl", "woman", "heroine", "breast", "boob", "vagina", "pussy", "labia" };
                var names = new List<string> { Norm(c.Key) };
                foreach (var t in c.BoneList) names.Add(Norm(t.name));
                foreach (var n in names) { foreach (var w in female) if (n.Contains(w)) { c.Female = true; break; } if (c.Female) break; }
            }

            if (_logged.Add(c.Key)) LogCharacter(c);
            return c;
        }

        // Character directions in the meshes' bind space (left from the thighs, up from pelvis to
        // chest, forward = their cross), then for each bone used by a direction slider: which of its
        // local axes is Width/Depth/Up, and what 1 m Up/Out/Forward is in its parent's local space.
        // Bind space doesn't move with the animation, so the axes don't flip as she bends.
        void Directions(Character c, Dictionary<Transform, Matrix4x4> bind, Dictionary<string, List<Transform>> rig)
        {
            Func<Transform, Vector3?> pos = t => { Matrix4x4 m; return t != null && bind.TryGetValue(t, out m) ? (Vector3?)m.inverse.GetColumn(3) : null; };
            List<Transform> th, hp, rc;
            Transform hipL = null, hipR = null, pelvis = null, chest = null;
            if (rig.TryGetValue("thighs", out th) && th.Count == 2) { hipL = th[0]; hipR = th[1]; }
            else { c.Bones.TryGetValue("Hip_L", out hipL); c.Bones.TryGetValue("Hip_R", out hipR); }
            if (rig.TryGetValue("hips", out hp)) pelvis = hp[0]; else c.Bones.TryGetValue("Root_M", out pelvis);
            if (rig.TryGetValue("ribcage", out rc)) chest = rc[0]; else c.Bones.TryGetValue("Chest_M", out chest);
            var pl = pos(hipL); var pr = pos(hipR); var pp = pos(pelvis); var pc = pos(chest);
            Vector3 left, up;
            if (pl.HasValue && pr.HasValue && pp.HasValue && pc.HasValue) { left = (pl.Value - pr.Value).normalized; up = (pc.Value - pp.Value).normalized; }
            else { left = Vector3.left; up = Vector3.up; Plugin.Log.LogInfo("Body directions for '" + c.Key + "': no hips/chest in the bind pose, assuming Y up, X right"); }
            var fwd = Vector3.Cross(up, left).normalized;
            up = Vector3.Cross(left, fwd).normalized;
            // Self-check: toes are in front of the ankles.
            Transform toe, ankle;
            if (c.Bones.TryGetValue("Toes_L", out toe) && c.Bones.TryGetValue("Ankle_L", out ankle) && pos(toe).HasValue && pos(ankle).HasValue
                && Vector3.Dot(pos(toe).Value - pos(ankle).Value, fwd) < 0f)
            {
                fwd = -fwd;
                Plugin.Log.LogInfo("Body directions for '" + c.Key + "': forward flipped (toes were behind the ankles)");
            }
            var center = pp ?? Vector3.zero;

            foreach (var d in Detail.All)
            {
                if (d.Dir == Dir.None) continue;
                foreach (var t in c.DetailBones[d.Id])
                {
                    Matrix4x4 bp;
                    if (!bind.TryGetValue(t, out bp)) continue;
                    if (!c.Axis.ContainsKey(t)) c.Axis[t] = new[] { Dominant(bp.MultiplyVector(left)), Dominant(bp.MultiplyVector(fwd)), Dominant(bp.MultiplyVector(up)) };
                    if (c.MoveDir.ContainsKey(t) || t.parent == null) continue;
                    // The parent's bind pose; for a parent outside the skin, derive it from this bone's.
                    Matrix4x4 pbp;
                    if (!bind.TryGetValue(t.parent, out pbp)) pbp = Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale) * bp;
                    var tp = pos(t);
                    float side = tp.HasValue && Vector3.Dot(tp.Value - center, left) < 0f ? -1f : 1f;
                    var dirs = new[] { up, left * side, fwd };
                    var m = new Vector3[3];
                    for (int i = 0; i < 3; i++)
                    {
                        var v = pbp.MultiplyVector(dirs[i]);
                        float world = t.parent.TransformVector(v).magnitude; // local units -> metres, now
                        m[i] = world > 1e-6f ? v / world : Vector3.zero;
                    }
                    c.MoveDir[t] = m; // [0] up, [1] out, [2] forward: local offset per metre
                }
            }
        }

        static int Dominant(Vector3 v)
        {
            float x = Mathf.Abs(v.x), y = Mathf.Abs(v.y), z = Mathf.Abs(v.z);
            return x >= y && x >= z ? 0 : y >= z ? 1 : 2;
        }

        // One description of each character per session, so slider bones can be tuned to the game's rig from a log.
        void LogCharacter(Character c)
        {
            var sb = new StringBuilder();
            sb.Append("Character '").Append(c.Key).Append("': ").Append(c.Female ? "female" : "not detected as female")
              .Append(", ").Append(c.Renderers.Count).Append(" skinned meshes, ").Append(c.BoneList.Count).Append(" bones")
              .Append(c.Look.Empty ? "" : ", saved look with " + c.Look.Count + " changes loaded");
            if (!c.Female) { Plugin.Log.LogInfo(sb.ToString()); return; }
            sb.Append("\n  sliders: ");
            foreach (var d in Detail.All)
            {
                sb.Append(d.Id).Append('=');
                var hits = c.DetailBones[d.Id];
                if (hits.Count == 0) sb.Append("none");
                for (int i = 0; i < hits.Count; i++)
                    sb.Append(i > 0 ? "/" : "").Append(hits[i].name).Append('(').Append(hits[i].parent != null ? hits[i].parent.name : "-").Append(',').Append(hits[i].childCount).Append(')');
                string off;
                if (c.Off.TryGetValue(d.Id, out off) && hits.Count > 0) sb.Append(" OFF: ").Append(off);
                sb.Append("; ");
            }
            sb.Append("\n  bones (parent): ");
            foreach (var t in c.BoneList) sb.Append(t.name).Append('(').Append(t.parent != null ? t.parent.name : "-").Append(") ");
            Plugin.Log.LogInfo(sb.ToString());
        }

        public void Touch(Character c) { c.Dirty = true; c.DirtyAt = Time.unscaledTime; }

        // ---- applying (every frame, after animation, before physics/IK) ----

        public void ApplyAll()
        {
            _frame++;
            if (_p.Apply.Value)
                foreach (var c in Found)
                    if (!c.Look.Empty && c.Root != null) Apply(c);
            Release();
        }

        void Apply(Character c)
        {
            _net.Clear(); _len.Clear(); _add.Clear(); _posTargets.Clear();
            foreach (var kv in c.Look.Bones)
            {
                Transform b;
                if (c.Bones.TryGetValue(kv.Key, out b) && b != null) Mul(b, kv.Value);
            }
            foreach (var d in Detail.All)
            {
                float v;
                if (!c.Look.Regions.TryGetValue(d.Id, out v) || Mathf.Abs(v - d.Neutral) < 0.0001f || c.Off.ContainsKey(d.Id)) continue;
                foreach (var b in c.DetailBones[d.Id])
                {
                    if (b == null) continue;
                    switch (d.Op)
                    {
                        case Op.Size:
                            Mul(b, new Vector3(v, v, v));
                            if (d.Keep) KeepBelow(b, Stem(b.name), new Vector3(v, v, v));
                            break;
                        case Op.Stretch:
                            int[] ax;
                            if (!c.Axis.TryGetValue(b, out ax)) break;
                            var f = Vector3.one;
                            f[d.Dir == Dir.Width ? ax[0] : d.Dir == Dir.Depth ? ax[1] : ax[2]] = v;
                            Mul(b, f);
                            if (d.Keep) KeepBelow(b, Stem(b.name), f);
                            break;
                        case Op.Move:
                            Vector3[] md;
                            if (!c.MoveDir.TryGetValue(b, out md)) break;
                            var dv = d.Dir == Dir.Up ? md[0] : d.Dir == Dir.Out ? md[1] : d.Dir == Dir.Forward ? md[2] : -md[2];
                            Vector3 a;
                            _add[b] = (_add.TryGetValue(b, out a) ? a : Vector3.zero) + dv * (v * 0.01f);
                            _posTargets.Add(b);
                            break;
                        case Op.Length:
                            LengthBelow(b, Stem(b.name), v);
                            break;
                    }
                }
            }

            foreach (var kv in _net)
            {
                var tr = kv.Key;
                ScaleTrack t;
                var s = tr.localScale;
                if (!_scales.TryGetValue(tr, out t)) { t = new ScaleTrack { T = tr, Rest = s, Last = new Vector3(float.NaN, 0, 0) }; _scales[tr] = t; }
                else if ((s - t.Last).sqrMagnitude > 1e-10f) t.Rest = s; // animation set it since our last write
                var target = Vector3.Scale(t.Rest, kv.Value);
                tr.localScale = target;
                t.Last = target;
                t.Used = _frame;
            }
            foreach (var tr in _posTargets)
            {
                PosTrack t;
                var p = tr.localPosition;
                if (!_poss.TryGetValue(tr, out t)) { t = new PosTrack { T = tr, Rest = p, Last = new Vector3(float.NaN, 0, 0) }; _poss[tr] = t; }
                else if ((p - t.Last).sqrMagnitude > 1e-12f) t.Rest = p; // animation or physics reset it since our last write
                float l; Vector3 a;
                var target = t.Rest * (_len.TryGetValue(tr, out l) ? l : 1f) + (_add.TryGetValue(tr, out a) ? a : Vector3.zero);
                tr.localPosition = target;
                t.Last = target;
                t.Used = _frame;
            }
        }

        void Mul(Transform t, Vector3 f)
        {
            Vector3 v;
            _net[t] = _net.TryGetValue(t, out v) ? Vector3.Scale(v, f) : f;
        }

        // Scales the next joints back (1/f), walking through the part's own helpers (HipPart1_L
        // under Hip_L), which keep the scale. f is in b's axes; mapped to each child's axes.
        void KeepBelow(Transform b, string stem, Vector3 f)
        {
            for (int i = 0; i < b.childCount; i++)
            {
                var ch = b.GetChild(i);
                var g = MapAxes(f, ch.localRotation);
                if (Norm(ch.name).StartsWith(stem)) KeepBelow(ch, stem, g);
                else Mul(ch, new Vector3(1f / g.x, 1f / g.y, 1f / g.z));
            }
        }

        // Pushes the next joints (and the part's helpers on the way) along the bone.
        void LengthBelow(Transform b, string stem, float f)
        {
            for (int i = 0; i < b.childCount; i++)
            {
                var ch = b.GetChild(i);
                float l;
                _len[ch] = _len.TryGetValue(ch, out l) ? l * f : f;
                _posTargets.Add(ch);
                if (Norm(ch.name).StartsWith(stem)) LengthBelow(ch, stem, f); // a helper in the chain (Hip -> HipPart1 -> HipPart2 -> Knee): each segment grows
            }
        }

        // A parent's per-axis factor as seen along each of the child's axes.
        static Vector3 MapAxes(Vector3 f, Quaternion q)
        {
            if (Mathf.Approximately(f.x, f.y) && Mathf.Approximately(f.y, f.z)) return f;
            var g = Vector3.one;
            for (int j = 0; j < 3; j++)
            {
                var e = Vector3.zero; e[j] = 1f;
                g[j] = f[Dominant(q * e)];
            }
            return g;
        }

        // Gives back the game's values for everything we no longer change.
        void Release()
        {
            _drop.Clear();
            foreach (var kv in _scales)
                if (kv.Value.Used != _frame)
                {
                    var t = kv.Value;
                    if (t.T != null && (t.T.localScale - t.Last).sqrMagnitude < 1e-10f) t.T.localScale = t.Rest;
                    _drop.Add(kv.Key);
                }
            foreach (var t in _drop) _scales.Remove(t);
            _drop.Clear();
            foreach (var kv in _poss)
                if (kv.Value.Used != _frame)
                {
                    var t = kv.Value;
                    if (t.T != null && (t.T.localPosition - t.Last).sqrMagnitude < 1e-12f) t.T.localPosition = t.Rest;
                    _drop.Add(kv.Key);
                }
            foreach (var t in _drop) _poss.Remove(t);
        }

        public void RestoreAll() { _frame++; Release(); }
    }
}
