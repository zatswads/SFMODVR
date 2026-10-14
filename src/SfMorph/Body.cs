using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // A body part VaM-style: a size slider that scales the bones whose names
    // match. "Keep" scales the part's children back, so only that part changes
    // size (a bigger thigh doesn't make a bigger calf and foot).
    internal class Region
    {
        public string Id, Label;
        public string[] Words, Not;
        public float Min, Max;
        public bool Keep;

        public static readonly Region[] All =
        {
            new Region { Id = "breasts", Label = "Breasts", Words = new[] { "breast", "boob", "bust", "pectoral", "mune", "oppai", "chichi" }, Not = new[] { "nipple" }, Min = 0.3f, Max = 2.5f },
            new Region { Id = "nipples", Label = "Nipples", Words = new[] { "nipple", "areola" }, Min = 0.3f, Max = 3f },
            new Region { Id = "glutes", Label = "Glutes", Words = new[] { "glute", "buttock", "butt", "siri" }, Min = 0.5f, Max = 2f },
            new Region { Id = "hips", Label = "Hips", Words = new[] { "pelvis", "hips" }, Min = 0.7f, Max = 1.5f, Keep = true },
            new Region { Id = "waist", Label = "Waist", Words = new[] { "waist", "abdomen", "belly", "stomach" }, Min = 0.6f, Max = 1.5f, Keep = true },
            new Region { Id = "ribcage", Label = "Ribcage", Words = new[] { "chest", "ribcage" }, Not = new[] { "breast" }, Min = 0.7f, Max = 1.4f, Keep = true },
            new Region { Id = "thighs", Label = "Thighs", Words = new[] { "thigh", "upperleg", "upleg" }, Min = 0.6f, Max = 1.6f, Keep = true },
            new Region { Id = "calves", Label = "Calves", Words = new[] { "calf", "shin", "lowerleg" }, Min = 0.6f, Max = 1.6f, Keep = true },
            new Region { Id = "upperarms", Label = "Upper arms", Words = new[] { "upperarm", "uparm", "shldr" }, Not = new[] { "twist" }, Min = 0.6f, Max = 1.6f, Keep = true },
            new Region { Id = "forearms", Label = "Forearms", Words = new[] { "forearm", "lowerarm" }, Min = 0.6f, Max = 1.6f, Keep = true },
            new Region { Id = "hands", Label = "Hands", Words = new[] { "hand" }, Not = new[] { "handle", "ik" }, Min = 0.7f, Max = 1.4f },
            new Region { Id = "feet", Label = "Feet", Words = new[] { "foot" }, Not = new[] { "ik" }, Min = 0.7f, Max = 1.4f },
            new Region { Id = "neck", Label = "Neck", Words = new[] { "neck" }, Min = 0.7f, Max = 1.5f, Keep = true },
            new Region { Id = "head", Label = "Head", Words = new[] { "head" }, Not = new[] { "headtop", "headend", "end" }, Min = 0.8f, Max = 1.25f },
        };

        public bool Matches(string norm)
        {
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
        public readonly Dictionary<string, List<Transform>> RegionBones = new Dictionary<string, List<Transform>>();
        public readonly HashSet<string> FromGame = new HashSet<string>(); // parts whose bones the game itself named
        public readonly List<Transform> Heads = new List<Transform>();
        // Shape names (after the last '.', so the body's and the clothes' copies of a morph are one slider).
        public readonly List<string> ShapeNames = new List<string>();
        public readonly Look Look = new Look();
        public bool Dirty;
        public float DirtyAt;
    }

    // Finds the characters (skinned meshes grouped by root, like SfNr's People),
    // keeps their looks, and applies them every frame after the game's animation.
    internal class Body
    {
        readonly Plugin _p;
        public readonly List<Character> Found = new List<Character>();
        public string Status = "looking for characters";
        float _nextScan;
        int _frame;
        readonly HashSet<string> _logged = new HashSet<string>();
        Transform _lady;

        // Blendshape indices per mesh, by short name (a name can exist more than once).
        readonly Dictionary<Mesh, Dictionary<string, List<int>>> _shapeIndex = new Dictionary<Mesh, Dictionary<string, List<int>>>();

        // What we wrote, so we can tell the game's own changes from ours and undo ours.
        class ShapeTrack { public SkinnedMeshRenderer R; public int I; public float Base, Last; public int Used; }
        class BoneTrack { public Transform T; public Vector3 Rest, Last; public int Used; }
        readonly Dictionary<long, ShapeTrack> _shapes = new Dictionary<long, ShapeTrack>();
        readonly Dictionary<Transform, BoneTrack> _bones = new Dictionary<Transform, BoneTrack>();
        readonly Dictionary<Transform, Vector3> _net = new Dictionary<Transform, Vector3>();
        readonly List<long> _dropShapes = new List<long>();
        readonly List<Transform> _dropBones = new List<Transform>();

        public Body(Plugin p) { _p = p; }

        public static string Norm(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        public static string ShortShape(string name) { int i = name.LastIndexOf('.'); return i >= 0 ? name.Substring(i + 1) : name; }

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
            Status = _lady != null && women > 0 ? "the game's woman found (" + Found.Find(x => x.Root == _lady).FromGame.Count + " body parts from the game's rig)"
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

            var skin = new HashSet<Transform>();
            var shapes = new HashSet<string>();
            foreach (var r in list)
            {
                foreach (var b in r.bones) if (b != null) skin.Add(b);
                if (r.rootBone != null) skin.Add(r.rootBone);
                foreach (var s in Index(r.sharedMesh).Keys) shapes.Add(s);
            }
            // Hierarchy order, so a parent comes before its children.
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (skin.Contains(t))
                {
                    c.BoneList.Add(t);
                    if (!c.Bones.ContainsKey(t.name)) c.Bones[t.name] = t;
                    if (Norm(t.name).Contains("head")) c.Heads.Add(t);
                }
            c.ShapeNames.AddRange(shapes);
            c.ShapeNames.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var reg in Region.All)
            {
                var hits = new List<Transform>();
                foreach (var t in c.BoneList)
                {
                    if (!reg.Matches(Norm(t.name))) continue;
                    // Only the top of a matching chain (Breast_L, not also Breast_L_02 under it).
                    bool under = false;
                    for (var a = t.parent; a != null && !under; a = a.parent) under = hits.Contains(a);
                    if (!under) hits.Add(t);
                }
                c.RegionBones[reg.Id] = hits;
            }
            // The game's own bone references beat name guesses.
            if (root == _lady)
                foreach (var kv in GameRig.Parts(_lady)) { c.RegionBones[kv.Key] = kv.Value; c.FromGame.Add(kv.Key); }

            var female = new[] { "lady", "female", "girl", "woman", "heroine", "breast", "boob", "vagina", "pussy", "labia" };
            string rootNorm = Norm(c.Key);
            if (_lady != null) c.Female = root == _lady; // the game has exactly one woman
            else foreach (var w in female) if (rootNorm.Contains(w)) c.Female = true;
            if (!c.Female && _lady == null) foreach (var t in c.BoneList) { var n = Norm(t.name); foreach (var w in female) if (n.Contains(w)) { c.Female = true; break; } if (c.Female) break; }
            if (!c.Female && _lady == null) foreach (var s in c.ShapeNames) { var n = Norm(s); foreach (var w in female) if (n.Contains(w)) { c.Female = true; break; } if (c.Female) break; }

            if (_logged.Add(c.Key + "/" + list.Count)) LogCharacter(c);
            return c;
        }

        // One-shot description of each new character, so the region names can be tuned to the game's rig from a log.
        void LogCharacter(Character c)
        {
            var sb = new StringBuilder();
            sb.Append("Character '").Append(c.Key).Append("': ").Append(c.Female ? "female" : "not detected as female")
              .Append(", ").Append(c.Renderers.Count).Append(" skinned meshes, ").Append(c.BoneList.Count).Append(" bones, ")
              .Append(c.ShapeNames.Count).Append(" shapes").Append(c.Look.Empty ? "" : ", saved look with " + c.Look.Count + " changes loaded");
            sb.Append("\n  meshes: ");
            foreach (var r in c.Renderers) sb.Append(r.name).Append(" (").Append(r.sharedMesh.blendShapeCount).Append(" shapes) ");
            sb.Append("\n  regions: ");
            foreach (var reg in Region.All)
            {
                sb.Append(reg.Label).Append('=');
                var hits = c.RegionBones[reg.Id];
                if (hits.Count == 0) sb.Append("none");
                for (int i = 0; i < hits.Count; i++) sb.Append(i > 0 ? "/" : "").Append(hits[i].name);
                sb.Append(c.FromGame.Contains(reg.Id) ? " (game rig); " : "; ");
            }
            sb.Append("\n  bones: ");
            foreach (var t in c.BoneList) sb.Append(t.name).Append(' ');
            sb.Append("\n  shapes: ");
            foreach (var s in c.ShapeNames) sb.Append(s).Append(", ");
            Plugin.Log.LogInfo(sb.ToString());
        }

        public Dictionary<string, List<int>> Index(Mesh m)
        {
            Dictionary<string, List<int>> d;
            if (_shapeIndex.TryGetValue(m, out d)) return d;
            d = new Dictionary<string, List<int>>();
            for (int i = 0; i < m.blendShapeCount; i++)
            {
                var n = ShortShape(m.GetBlendShapeName(i));
                List<int> l;
                if (!d.TryGetValue(n, out l)) { l = new List<int>(); d[n] = l; }
                l.Add(i);
            }
            _shapeIndex[m] = d;
            return d;
        }

        // The game's weight of a shape right now (before our addition), for the panel.
        public float GameWeight(Character c, string shape)
        {
            foreach (var r in c.Renderers)
            {
                if (r == null || r.sharedMesh == null) continue;
                List<int> idx;
                if (!Index(r.sharedMesh).TryGetValue(shape, out idx)) continue;
                ShapeTrack t;
                if (_shapes.TryGetValue(Id(r, idx[0]), out t)) return t.Base;
                return r.GetBlendShapeWeight(idx[0]);
            }
            return 0f;
        }

        public void Touch(Character c) { c.Dirty = true; c.DirtyAt = Time.unscaledTime; }

        // ---- applying (every LateUpdate) ----

        static long Id(SkinnedMeshRenderer r, int i) { return ((long)r.GetInstanceID() << 16) ^ i; }

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
            foreach (var kv in c.Look.Shapes)
                foreach (var r in c.Renderers)
                {
                    if (r == null || r.sharedMesh == null) continue;
                    List<int> idx;
                    if (!Index(r.sharedMesh).TryGetValue(kv.Key, out idx)) continue;
                    foreach (var i in idx)
                    {
                        long id = Id(r, i);
                        ShapeTrack t;
                        float w = r.GetBlendShapeWeight(i);
                        if (!_shapes.TryGetValue(id, out t)) { t = new ShapeTrack { R = r, I = i, Base = w, Last = float.NaN }; _shapes[id] = t; }
                        else if (Mathf.Abs(w - t.Last) > 0.001f) t.Base = w; // the game set it since our last write
                        float target = t.Base + kv.Value;
                        if (Mathf.Abs(target - w) > 0.0001f) r.SetBlendShapeWeight(i, target);
                        t.Last = target;
                        t.Used = _frame;
                    }
                }

            // Bone sizes: each bone's own factor times its region's; "Keep" parts scale their children back.
            _net.Clear();
            foreach (var kv in c.Look.Bones)
            {
                Transform b;
                if (c.Bones.TryGetValue(kv.Key, out b) && b != null) Mul(b, kv.Value);
            }
            foreach (var reg in Region.All)
            {
                float f;
                if (!c.Look.Regions.TryGetValue(reg.Id, out f) || Mathf.Abs(f - 1f) < 0.0001f) continue;
                foreach (var b in c.RegionBones[reg.Id])
                {
                    if (b == null) continue;
                    Mul(b, new Vector3(f, f, f));
                    if (reg.Keep) for (int i = 0; i < b.childCount; i++) Mul(b.GetChild(i), new Vector3(1f / f, 1f / f, 1f / f));
                }
            }
            foreach (var kv in _net)
            {
                var tr = kv.Key;
                BoneTrack t;
                var s = tr.localScale;
                if (!_bones.TryGetValue(tr, out t)) { t = new BoneTrack { T = tr, Rest = s, Last = new Vector3(float.NaN, 0, 0) }; _bones[tr] = t; }
                else if ((s - t.Last).sqrMagnitude > 1e-10f) t.Rest = s; // animation set it since our last write
                var target = Vector3.Scale(t.Rest, kv.Value);
                tr.localScale = target;
                t.Last = target;
                t.Used = _frame;
            }
        }

        void Mul(Transform t, Vector3 f)
        {
            Vector3 v;
            _net[t] = _net.TryGetValue(t, out v) ? Vector3.Scale(v, f) : f;
        }

        // Gives back the game's values for everything we no longer change.
        void Release()
        {
            _dropShapes.Clear();
            foreach (var kv in _shapes)
                if (kv.Value.Used != _frame)
                {
                    var t = kv.Value;
                    if (t.R != null && Mathf.Abs(t.R.GetBlendShapeWeight(t.I) - t.Last) < 0.001f) t.R.SetBlendShapeWeight(t.I, t.Base);
                    _dropShapes.Add(kv.Key);
                }
            foreach (var id in _dropShapes) _shapes.Remove(id);
            _dropBones.Clear();
            foreach (var kv in _bones)
                if (kv.Value.Used != _frame)
                {
                    var t = kv.Value;
                    if (t.T != null && (t.T.localScale - t.Last).sqrMagnitude < 1e-10f) t.T.localScale = t.Rest;
                    _dropBones.Add(kv.Key);
                }
            foreach (var t in _dropBones) _bones.Remove(t);
        }

        public void RestoreAll() { _frame++; Release(); }
    }
}
