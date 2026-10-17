using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.Rendering;

namespace SfMorph
{
    // Avatar replacement (docs/SOURCES.md, 'A third mode: avatar replacement'): a character's own mesh, rig and textures from an
    // avatar bundle (tools/avatar/build.ps1) drawn instead of her and driven by her pose every frame.
    //
    // Her skeleton keeps animating as before (the game's Animator, FinalIK, the face Animator, our Overlap / Embody / Reactions):
    // it is only the pose source. Her renderers are hidden with forceRenderingOff (the game's own SetActive swaps keep working
    // under it); the avatar is a top-level 'SfAvatar <name>' object (every scan skips 'Sf' objects) that follows her root.
    //
    // The retarget is the one tools/avatar/retarget_check.py checks offline (Blender, on her export and a PoseRecorder
    // recording), in her root's space: D_h = her bone's rotation now x its rotation in her bind pose^-1;
    //   aligned rest A_b: the avatar's rest turned bone by bone (parents first) so each limb / finger segment points where hers
    //                     does in her bind pose (the hand by two directions); trunk, collars, eyes and jaw are not turned
    //                     (their directions are rig conventions);
    //   mapped    W_b = D_h A_b;   trunk bones between anchors (G9 spine1 / spine3 / neck2)  W_b = D_lo (D_lo^-1 D_hi)^f A_b;
    //   twist     the upper arm / thigh keeps its swing, its twist bones take 1/n..1 of its twist; forearm twist bones take
    //             k/(n+1) of the hand's twist about the forearm;
    //   others    keep their rest local rotation (metacarpals, toes, the face rig, pectorals);
    //   root      her pelvis + D_root (avatar hips - her pelvis at bind); uniform scale by the legs (or height, or none);
    //   IK        optional two-bone IK of arms / legs toward her wrists / ankles (pole: her elbow / knee), by weight.
    // It runs in a PlayerLoop step right before PostLateUpdate.UpdateAllSkinnedMeshes, where PoseRecorder samples: after the
    // Animator, FinalIK, DynamicBone, the face Animator and our own steps.
    internal static class Avatar
    {
        internal struct DriveStep { }

        public static ConfigEntry<string> Pick;
        public static ConfigEntry<int> ScaleMode, MaterialRoute;
        public static ConfigEntry<float> Brightness, HandIk, FootIk;
        public static ConfigEntry<bool> KeepHair;

        public static void Bind(ConfigFile cfg)
        {
            Pick = cfg.Bind("Avatar", "Name", "", "Avatar replacement: the avatar drawn instead of her (a folder under BepInEx\\config\\SfMorphs\\avatars made by tools/avatar/build.ps1). Empty: off.");
            ScaleMode = cfg.Bind("Avatar", "Scale", 0, new ConfigDescription("Avatar size: 0 her leg length (its feet on her floor), 1 her height, 2 its own size.", new AcceptableValueRange<int>(0, 2)));
            MaterialRoute = cfg.Bind("Avatar", "Material", 1, new ConfigDescription("Avatar skin shader: 0 a copy of her own skin material (Shader Graphs/Skin) with the avatar's textures and her makeup / blush layers off, 1 the game's HDRP/Lit with subsurface scattering and her skin's diffusion profile.", new AcceptableValueRange<int>(0, 1)));
            Brightness = cfg.Bind("Avatar", "Brightness", 1f, new ConfigDescription("Avatar skin brightness (multiplies its colour maps).", new AcceptableValueRange<float>(0.5f, 1.5f)));
            HandIk = cfg.Bind("Avatar", "HandIk", 0f, new ConfigDescription("Pull the avatar's hands onto hers (0 = its own arm lengths, 1 = exactly where hers are; elbows bend to make up the difference).", new AcceptableValueRange<float>(0f, 1f)));
            FootIk = cfg.Bind("Avatar", "FootIk", 0f, new ConfigDescription("Pull the avatar's feet onto hers (0 = its own leg lengths, 1 = exactly where hers are).", new AcceptableValueRange<float>(0f, 1f)));
            KeepHair = cfg.Bind("Avatar", "KeepHair", true, "Keep her hair on while the avatar is drawn (it follows her head, which sits within a few cm of the avatar's).");
        }

        public static string Root { get { return Path.Combine(Store.Dir, "avatars"); } }
        public static string Status = "";

        // ---- the avatars on disk --------------------------------------------------------------------------------------------
        public class Entry { public string Name, Dir, Bundle, Info; }
        static List<Entry> _entries;
        public static List<Entry> Entries { get { if (_entries == null) Rescan(); return _entries; } }

        public static void Rescan()
        {
            var l = new List<Entry>();
            try
            {
                if (Directory.Exists(Root))
                    foreach (var d in Directory.GetDirectories(Root))
                    {
                        var man = Path.Combine(d, "avatar.txt");
                        if (!File.Exists(man)) continue;
                        var lines = File.ReadAllLines(man);
                        string name = Val(lines, "name") ?? Path.GetFileName(d), bundle = Val(lines, "bundle");
                        string path = bundle != null ? Path.Combine(d, bundle) : null;
                        if (path == null || !File.Exists(path))
                            path = Directory.GetFiles(d).FirstOrDefault(f => Path.GetExtension(f).Length == 0);
                        if (path == null) continue;
                        l.Add(new Entry { Name = name, Dir = d, Bundle = path, Info = (Val(lines, "height") ?? "?").Split(' ')[0] + " m" });
                    }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Avatars: couldn't list " + Root + ": " + e.Message); }
            _entries = l.OrderBy(e => e.Name).ToList();
        }

        static string Val(string[] lines, string key)
        {
            foreach (var l in lines) if (l.StartsWith(key + " ", StringComparison.Ordinal)) return l.Substring(key.Length + 1).Trim();
            return null;
        }

        // ---- per frame: pick, load, keep ----------------------------------------------------------------------------------------
        static Rig _rig;
        static float _nextCheck;
        static string _failed;   // the pick that failed to load (not retried until the pick or her root changes)
        static Transform _failedRoot;

        public static bool On { get { return _rig != null; } }

        // From Runner.Update: loads / unloads by the config, keeps the frame-loop step and the hidden set every 2 s.
        public static void Tick(Body body)
        {
            try
            {
                Character her = null;
                foreach (var c in body.Found) if (her == null && c.Female && !c.IsPlayer && c.Root != null) her = c;
                string want = (Pick.Value ?? "").Trim();
                string off = want.Length == 0 ? "" : her == null ? "waiting for her" : her.Look.Morph != null ? "off: a converted character is on (pick None there first)" : null;
                if (off != null)
                {
                    if (_rig != null) Unload(off.Length > 0 ? off : "turned off");
                    Status = off;
                    return;
                }
                if (_rig != null && (_rig.HerRoot != her.Root || _rig.Name != want || _rig.Go == null || _rig.ScaleMode != ScaleMode.Value))
                    Unload(_rig.Go == null ? "its object went away (scene change)" : _rig.HerRoot != her.Root ? "her character changed" : "settings changed");
                if (_rig == null)
                {
                    if (_failed == want && _failedRoot == her.Root) return;
                    Load(want, her);
                    if (_rig == null) { _failed = want; _failedRoot = her.Root; }
                    else _failed = null;
                    return;
                }
                if (_rig.Route != MaterialRoute.Value || Mathf.Abs(_rig.Bright - Brightness.Value) > 0.001f) _rig.Materials(MaterialRoute.Value, Brightness.Value);
                if (_rig.Hair != KeepHair.Value) _rig.Hide(KeepHair.Value);
                if (Time.unscaledTime >= _nextCheck)
                {
                    _nextCheck = Time.unscaledTime + 2f;
                    EnsureStep();
                    _rig.Hide(KeepHair.Value);   // renderers the game added since (outfit swaps)
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Avatar failed, turned off: " + e);
                Status = "off: " + e.Message;
                try { Unload("error"); } catch { }
                Pick.Value = "";
            }
        }

        static void Load(string name, Character her)
        {
            var e = Entries.FirstOrDefault(x => x.Name == name);
            if (e == null) { Rescan(); e = Entries.FirstOrDefault(x => x.Name == name); }
            if (e == null) { Status = "off: no avatar '" + name + "' in " + Root; Plugin.Log.LogWarning("Avatar: " + Status); return; }
            var rig = new Rig();
            string err = rig.Build(e, her);
            if (err != null)
            {
                Status = "off: " + err;
                Plugin.Log.LogWarning("Avatar '" + name + "': " + err);
                rig.Destroy();
                return;
            }
            _rig = rig;
            EnsureStep();
            Status = rig.Summary;
        }

        static void Unload(string why)
        {
            if (_rig == null) return;
            Plugin.Log.LogInfo("Avatar '" + _rig.Name + "' off: " + why);
            var r = _rig; _rig = null;
            r.Destroy();
            RemoveStep();
        }

        static int _errors;
        static void Step()
        {
            if (_rig == null) return;
            try { _rig.Drive(); }
            catch (Exception e)
            {
                if (++_errors <= 3) Plugin.Log.LogError("Avatar drive failed: " + e);
                if (_errors >= 3) { Status = "off: drive failed (" + e.Message + ")"; Pick.Value = ""; try { Unload("drive errors"); } catch { } }
            }
        }

        // ---- PlayerLoop (the PoseRecorder pattern): right before the game skins the meshes -------------------------------------
        static void EnsureStep()
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var subs = root.subSystemList;
            if (subs == null) return;
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type != typeof(PostLateUpdate) || subs[i].subSystemList == null) continue;
                var list = new List<PlayerLoopSystem>(subs[i].subSystemList);
                if (list.Exists(s => s.type == typeof(DriveStep))) return;
                int at = list.FindIndex(s => s.type == typeof(PostLateUpdate.UpdateAllSkinnedMeshes));
                var step = new PlayerLoopSystem { type = typeof(DriveStep), updateDelegate = Step };
                if (at < 0) list.Add(step); else list.Insert(at, step);
                subs[i].subSystemList = list.ToArray();
                root.subSystemList = subs;
                PlayerLoop.SetPlayerLoop(root);
                return;
            }
        }

        static void RemoveStep()
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var subs = root.subSystemList;
            if (subs == null) return;
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type != typeof(PostLateUpdate) || subs[i].subSystemList == null) continue;
                var list = new List<PlayerLoopSystem>(subs[i].subSystemList);
                if (list.RemoveAll(s => s.type == typeof(DriveStep)) == 0) return;
                subs[i].subSystemList = list.ToArray();
                root.subSystemList = subs;
                PlayerLoop.SetPlayerLoop(root);
                return;
            }
        }

        // ---- bone tables: canonical key -> names ('{s}' = the rig's side token) -------------------------------------------------
        static readonly string[] Keys = { "hips", "spine", "chest", "neck", "head", "jaw",
            "eye", "collar", "upperarm", "forearm", "hand", "thumb1", "thumb2", "thumb3", "index1", "index2", "index3", "middle1", "middle2", "middle3",
            "ring1", "ring2", "ring3", "pinky1", "pinky2", "pinky3", "thigh", "shin", "foot", "toe" };
        const int FirstSided = 6;

        class RigTable { public string Id; public string L, R; public Dictionary<string, string[]> Names; }

        static Dictionary<string, string[]> Fingers(string thumb, string index, string mid, string ring, string pinky)
        {
            var d = new Dictionary<string, string[]>();
            for (int k = 1; k <= 3; k++)
            {
                string n = k.ToString(CultureInfo.InvariantCulture);
                d["thumb" + k] = new[] { thumb.Replace("{0}", n) }; d["index" + k] = new[] { index.Replace("{0}", n) }; d["middle" + k] = new[] { mid.Replace("{0}", n) };
                d["ring" + k] = new[] { ring.Replace("{0}", n) }; d["pinky" + k] = new[] { pinky.Replace("{0}", n) };
            }
            return d;
        }

        static RigTable Table(string id, string l, string r, Dictionary<string, string[]> fingers, params object[] kv)
        {
            var t = new RigTable { Id = id, L = l, R = r, Names = new Dictionary<string, string[]>(fingers) };
            for (int i = 0; i + 1 < kv.Length; i += 2) t.Names[(string)kv[i]] = ((string)kv[i + 1]).Split('|');
            return t;
        }

        // hers: AdvancedSkeleton, side as a suffix
        static readonly RigTable Hers = Table("as", "_L", "_R", Fingers("ThumbFinger{0}{s}", "IndexFinger{0}{s}", "MiddleFinger{0}{s}", "RingFinger{0}{s}", "PinkyFinger{0}{s}"),
            "hips", "Root_M", "spine", "Spine1_M", "chest", "Chest_M", "neck", "Neck_M", "head", "Head_M", "jaw", "Jaw_M", "eye", "Eye{s}",
            "collar", "Scapula{s}", "upperarm", "Shoulder{s}", "forearm", "Elbow{s}", "hand", "Wrist{s}",
            "thigh", "Hip{s}", "shin", "Knee{s}", "foot", "Ankle{s}", "toe", "Toes{s}");

        // the avatars' (tools/sources/humanoid.py's tables): DAZ Genesis 9, Genesis 3 / 8 / 8.1, Genesis 2 (VaM)
        static readonly RigTable[] AvatarTables =
        {
            Table("g9", "l_", "r_", Fingers("{s}thumb{0}", "{s}index{0}", "{s}mid{0}", "{s}ring{0}", "{s}pinky{0}"),
                "hips", "hip", "spine", "spine2|spine1", "chest", "spine4|spine3", "neck", "neck1", "head", "head", "jaw", "lowerjaw", "eye", "{s}eye",
                "collar", "{s}shoulder", "upperarm", "{s}upperarm", "forearm", "{s}forearm", "hand", "{s}hand",
                "thigh", "{s}thigh", "shin", "{s}shin", "foot", "{s}foot", "toe", "{s}toes|{s}toe"),
            Table("g8", "l", "r", Fingers("{s}Thumb{0}", "{s}Index{0}", "{s}Mid{0}", "{s}Ring{0}", "{s}Pinky{0}"),
                "hips", "hip", "spine", "abdomenUpper|abdomen2", "chest", "chestUpper|chestLower|chest", "neck", "neckLower|neck", "head", "head",
                "jaw", "lowerJaw", "eye", "{s}Eye", "collar", "{s}Collar", "upperarm", "{s}ShldrBend|{s}Shldr", "forearm", "{s}ForearmBend|{s}ForeArm",
                "hand", "{s}Hand", "thigh", "{s}ThighBend|{s}Thigh", "shin", "{s}Shin", "foot", "{s}Foot", "toe", "{s}Toe"),
            Table("g2", "l", "r", Fingers("{s}Thumb{0}", "{s}Index{0}", "{s}Mid{0}", "{s}Ring{0}", "{s}Pinky{0}"),
                "hips", "hip", "spine", "abdomen2", "chest", "chest", "neck", "neck", "head", "head", "jaw", "lowerJaw", "eye", "{s}Eye",
                "collar", "{s}Collar", "upperarm", "{s}Shldr", "forearm", "{s}ForeArm", "hand", "{s}Hand",
                "thigh", "{s}Thigh", "shin", "{s}Shin", "foot", "{s}Foot", "toe", "{s}Toe"),
        };

        // key (+ side 'L' / 'R' for sided keys) -> bone, from a name lookup (case-insensitive)
        static Dictionary<string, Transform> MapBones(RigTable t, Dictionary<string, Transform> byName)
        {
            var m = new Dictionary<string, Transform>();
            for (int ki = 0; ki < Keys.Length; ki++)
            {
                string key = Keys[ki];
                if (!t.Names.TryGetValue(key, out var cands)) continue;
                foreach (var side in ki >= FirstSided ? new[] { "L", "R" } : new string[] { null })
                    foreach (var c in cands)
                    {
                        string n = c.Replace("{s}", side == null ? "" : side == "L" ? t.L : t.R);
                        if (byName.TryGetValue(n.ToLowerInvariant(), out var tr)) { m[side == null ? key : key + "." + side] = tr; break; }
                    }
            }
            return m;
        }

        static string K(string key, string side) { return side == null ? key : key + "." + side; }

        // ---- quaternion helpers ---------------------------------------------------------------------------------------------
        static Quaternion Pow(Quaternion q, float f)
        {
            q.ToAngleAxis(out float ang, out Vector3 axis);
            if (float.IsNaN(axis.x) || axis.sqrMagnitude < 1e-12f) return Quaternion.identity;
            if (ang > 180f) ang -= 360f;
            return Quaternion.AngleAxis(ang * f, axis);
        }

        // twist part of q about u (q = swing x twist)
        static Quaternion TwistAbout(Quaternion q, Vector3 u)
        {
            u.Normalize();
            var p = Vector3.Project(new Vector3(q.x, q.y, q.z), u);
            var t = new Quaternion(p.x, p.y, p.z, q.w);
            float n = Mathf.Sqrt(t.x * t.x + t.y * t.y + t.z * t.z + t.w * t.w);
            if (n < 1e-9f) return Quaternion.identity;
            return new Quaternion(t.x / n, t.y / n, t.z / n, t.w / n);
        }

        // ---- one loaded avatar ------------------------------------------------------------------------------------------------
        class Rig
        {
            public string Name, Summary = "";
            public Transform HerRoot;
            public GameObject Go;
            public int ScaleMode, Route = -1;
            public float Bright = -1f;
            public bool Hair = true;
            AssetBundle _bundle;
            GameObject _model;
            readonly List<Material> _made = new List<Material>();
            readonly List<Mesh> _meshes = new List<Mesh>();
            readonly Dictionary<Renderer, bool> _hidden = new Dictionary<Renderer, bool>();
            SkinnedMeshRenderer[] _rends;
            Material[][] _origMats; Mesh[] _origMesh;   // the bundle's, for rebuilding the materials
            readonly Dictionary<string, string> _surfRole = new Dictionary<string, string>();
            readonly Dictionary<string, Dictionary<string, string>> _surfTex = new Dictionary<string, Dictionary<string, string>>();
            readonly Dictionary<string, Color> _surfCol = new Dictionary<string, Color>();
            Material _herSkin;

            // avatar skeleton, parents first
            Transform[] _t; int[] _p;
            Quaternion[] _g, _al, _w; Vector3[] _h, _x;
            bool[] _fixed, _write;
            int[] _kind, _her, _lo, _hi; float[] _f;
            const int Follow = 0, Mapped = 1, Spread = 2, Twist = 3;
            class TwistSet { public int Bend, To, Hand; public bool HandMode; public int[] Bones; public float[] F; }
            readonly List<TwistSet> _twists = new List<TwistSet>();
            int _hips, _hipsHer;
            Vector3 _rootOff;
            readonly List<int[]> _arms = new List<int[]>(), _legs = new List<int[]>();   // avatar upper, mid, end + her upper, mid, end
            // her bones used
            readonly List<Transform> _ht = new List<Transform>();
            readonly List<Quaternion> _hb = new List<Quaternion>();
            readonly List<Vector3> _hp = new List<Vector3>();
            Quaternion[] _d; Vector3[] _hx;

            public string Build(Entry e, Character her)
            {
                Name = e.Name; HerRoot = her.Root; ScaleMode = Avatar.ScaleMode.Value;
                var lines = File.ReadAllLines(Path.Combine(e.Dir, "avatar.txt"));
                _bundle = AssetBundle.LoadFromFile(e.Bundle);
                if (_bundle == null) return "could not load " + e.Bundle + " (built for another Unity version, or already loaded)";
                string prefabName = Val(lines, "prefab") ?? Name;
                var prefab = _bundle.LoadAsset<GameObject>(prefabName);
                if (prefab == null) return "no prefab '" + prefabName + "' in " + e.Bundle;
                foreach (var l in lines)
                {
                    if (!l.StartsWith("surface ", StringComparison.Ordinal)) continue;
                    var f = l.Substring(8).Split('\t');
                    if (f.Length < 2) continue;
                    _surfRole[f[0]] = f[1];
                    var tex = new Dictionary<string, string>();
                    if (f.Length > 2)
                        foreach (var kv in f[2].Split(','))
                        {
                            int eq = kv.IndexOf('=');
                            if (eq > 0) tex[kv.Substring(0, eq)] = kv.Substring(eq + 1);
                        }
                    _surfTex[f[0]] = tex;
                    var col = Color.white;
                    if (f.Length > 3 && f[3].StartsWith("color=", StringComparison.Ordinal))
                    {
                        var c = f[3].Substring(6).Split(' ')[0].Split(',');
                        if (c.Length == 3) col = new Color(P(c[0]), P(c[1]), P(c[2]), 1f);
                    }
                    _surfCol[f[0]] = col;
                }

                Go = new GameObject("SfAvatar " + Name);
                Go.transform.SetPositionAndRotation(HerRoot.position, HerRoot.rotation);
                _model = UnityEngine.Object.Instantiate(prefab, Go.transform, false);
                _model.name = "model";
                _rends = _model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (_rends.Length == 0) return "the prefab has no skinned meshes";
                _origMats = _rends.Select(r => r.sharedMaterials).ToArray();
                _origMesh = _rends.Select(r => r.sharedMesh).ToArray();

                // bones: the avatar's by name, hers by name (her root, not ours)
                var avByName = new Dictionary<string, Transform>();
                foreach (var t in _model.GetComponentsInChildren<Transform>(true)) { var k = t.name.ToLowerInvariant(); if (!avByName.ContainsKey(k)) avByName[k] = t; }
                var herByName = new Dictionary<string, Transform>();
                foreach (var t in HerRoot.GetComponentsInChildren<Transform>(true)) { var k = t.name.ToLowerInvariant(); if (!herByName.ContainsKey(k)) herByName[k] = t; }
                var hm = MapBones(Hers, herByName);
                RigTable best = null; Dictionary<string, Transform> am = null;
                foreach (var t in AvatarTables) { var m = MapBones(t, avByName); if (am == null || m.Count > am.Count) { am = m; best = t; } }
                var need = new[] { "hips", "spine", "neck", "head" }.Concat(new[] { "upperarm", "forearm", "hand", "thigh", "shin", "foot" }.SelectMany(k => new[] { k + ".L", k + ".R" }));
                var missA = need.Where(k => !am.ContainsKey(k)).ToList();
                var missH = need.Where(k => !hm.ContainsKey(k)).ToList();
                if (missA.Count > 0) return "the avatar's rig (" + best.Id + " table) has no " + string.Join(", ", missA.ToArray());
                if (missH.Count > 0) return "her rig has no " + string.Join(", ", missH.ToArray());

                // her bind pose in her root's space, from her renderers' bind poses (a bone in several meshes: the most common;
                // a side bone placed differently by copies, Eye_R: the one mirroring its counterpart, as rig.py does)
                var herBind = HerBind();

                // avatar skeleton: everything under the common ancestor of the mapped bones' top
                Transform top = am["hips"];
                while (top.parent != null && top.parent != _model.transform && top.parent.GetComponent<Renderer>() == null && top.parent.childCount == 1) top = top.parent;
                var order = new List<Transform>();
                var idx = new Dictionary<Transform, int>();
                void Walk(Transform t) { idx[t] = order.Count; order.Add(t); for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i)); }
                Walk(top);
                int n = order.Count;
                _t = order.ToArray(); _p = new int[n];
                for (int i = 0; i < n; i++) _p[i] = _t[i].parent != null && idx.TryGetValue(_t[i].parent, out var pi) ? pi : -1;
                _g = new Quaternion[n]; _al = new Quaternion[n]; _w = new Quaternion[n]; _h = new Vector3[n]; _x = new Vector3[n];
                _fixed = new bool[n]; _write = new bool[n]; _kind = new int[n]; _her = new int[n]; _lo = new int[n]; _hi = new int[n]; _f = new float[n];
                for (int i = 0; i < n; i++) { _her[i] = _lo[i] = _hi[i] = -1; }

                int HerIndex(Transform t)
                {
                    int i = _ht.IndexOf(t);
                    if (i >= 0) return i;
                    if (!herBind.TryGetValue(t, out var b)) return -1;
                    _ht.Add(t); _hb.Add(b.Value); _hp.Add(b.Key);
                    return _ht.Count - 1;
                }

                // facing: turn the model about up so its feet point where hers do; then the scale
                ReadRest();
                Vector3 Fwd(Func<string, Vector3> pos, string toe, string foot) { var v = pos(toe) - pos(foot); v.y = 0f; return v; }
                Vector3 avPos(string k) { return am.TryGetValue(k, out var t) ? _h[idx[t]] : Vector3.zero; }
                Vector3 herPos(string k) { return hm.TryGetValue(k, out var t) && herBind.TryGetValue(t, out var b) ? b.Key : Vector3.zero; }
                bool toes = am.ContainsKey("toe.L") && hm.ContainsKey("toe.L");
                var avF = toes ? Fwd(avPos, "toe.L", "foot.L") + Fwd(avPos, "toe.R", "foot.R") : Vector3.Cross(avPos("thigh.L") - avPos("thigh.R"), Vector3.up);
                var herF = toes ? Fwd(herPos, "toe.L", "foot.L") + Fwd(herPos, "toe.R", "foot.R") : Vector3.Cross(herPos("thigh.L") - herPos("thigh.R"), Vector3.up);
                float yaw = avF.sqrMagnitude > 1e-8f && herF.sqrMagnitude > 1e-8f ? Vector3.SignedAngle(avF, herF, Vector3.up) : 0f;
                _model.transform.localRotation = Quaternion.AngleAxis(yaw, Vector3.up) * _model.transform.localRotation;
                ReadRest();
                float scale = 1f;
                if (ScaleMode == 0) scale = (Mean(herPos, "thigh") - Mean(herPos, "foot")) / Mathf.Max(1e-4f, Mean(avPos, "thigh") - Mean(avPos, "foot"));
                else if (ScaleMode == 1) scale = herPos("head").y / Mathf.Max(1e-4f, avPos("head").y);
                if (!(scale > 0.3f && scale < 3f)) scale = 1f;
                Go.transform.localScale = Vector3.one * scale;
                ReadRest();

                // roles
                string[] trunk = { "hips", "spine", "chest", "neck", "head", "jaw" };
                var next = new Dictionary<string, string> { { "upperarm", "forearm" }, { "forearm", "hand" }, { "thigh", "shin" }, { "shin", "foot" }, { "foot", "toe" } };
                var to = new int[n]; var toHer = new int[n]; var across = new int[n][];
                for (int i = 0; i < n; i++) to[i] = toHer[i] = -1;
                int mapped = 0;
                foreach (var key in Keys)
                    foreach (var side in Array.IndexOf(Keys, key) >= FirstSided ? new[] { "L", "R" } : new string[] { null })
                    {
                        if (!am.TryGetValue(K(key, side), out var at) || !hm.TryGetValue(K(key, side), out var ht)) continue;
                        int i = idx.TryGetValue(at, out var ii) ? ii : -1;
                        int h = HerIndex(ht);
                        if (i < 0 || h < 0) continue;
                        _kind[i] = Mapped; _her[i] = h; mapped++;
                        string nk = null;
                        if (next.ContainsKey(key)) nk = next[key];
                        else if (key.Length > 1 && char.IsDigit(key[key.Length - 1]) && key[key.Length - 1] < '3') nk = key.Substring(0, key.Length - 1) + (char)(key[key.Length - 1] + 1);
                        else if (key == "hand") nk = "middle1";
                        if (nk != null && am.TryGetValue(K(nk, side), out var a2) && hm.TryGetValue(K(nk, side), out var h2) && idx.ContainsKey(a2))
                        {
                            int hh = HerIndex(h2);
                            if (hh >= 0) { to[i] = idx[a2]; toHer[i] = hh; }
                        }
                        if (key == "hand" && am.TryGetValue(K("pinky1", side), out var ap) && am.TryGetValue(K("index1", side), out var ai)
                            && hm.TryGetValue(K("pinky1", side), out var hp) && hm.TryGetValue(K("index1", side), out var hi2) && idx.ContainsKey(ap) && idx.ContainsKey(ai))
                        {
                            int a = HerIndex(hp), b = HerIndex(hi2);
                            if (a >= 0 && b >= 0) across[i] = new[] { idx[ap], idx[ai], a, b };
                        }
                        if (Array.IndexOf(trunk, key) >= 0 || key == "eye" || key == "collar") to[i] = toHer[i] = -1;   // change only
                    }
                // trunk bones between anchors on the hips -> head path share their neighbours' bend
                _hips = idx[am["hips"]]; _hipsHer = _her[_hips];
                var path = new List<int>();
                for (int i = idx[am["head"]]; i >= 0; i = _p[i]) { path.Insert(0, i); if (i == _hips) break; }
                var anchors = path.Where(i => _kind[i] == Mapped).ToList();
                int spread = 0;
                for (int a = 0; a + 1 < anchors.Count; a++)
                {
                    int i0 = path.IndexOf(anchors[a]), i1 = path.IndexOf(anchors[a + 1]);
                    for (int j = i0 + 1; j < i1; j++) { int b = path[j]; _kind[b] = Spread; _lo[b] = _her[anchors[a]]; _hi[b] = _her[anchors[a + 1]]; _f[b] = (j - i0) / (float)(i1 - i0); spread++; }
                }
                // twist bones: children of the upper arm / thigh (their own twist) and the forearm (the hand's)
                int twists = 0;
                foreach (var key in new[] { "upperarm", "thigh", "forearm" })
                    foreach (var side in new[] { "L", "R" })
                    {
                        if (!am.TryGetValue(K(key, side), out var bt) || !idx.TryGetValue(bt, out var bi) || to[bi] < 0) continue;
                        var tw = new List<int>();
                        for (int c = 0; c < bt.childCount; c++)
                        {
                            var ch = bt.GetChild(c);
                            if (ch.name.ToLowerInvariant().Contains("twist") && idx.TryGetValue(ch, out var ci) && _kind[ci] == Follow) tw.Add(ci);
                        }
                        if (tw.Count == 0) continue;
                        tw.Sort((x, y) => (_h[x] - _h[bi]).sqrMagnitude.CompareTo((_h[y] - _h[bi]).sqrMagnitude));
                        bool hand = key == "forearm";
                        int handI = am.TryGetValue(K("hand", side), out var htr) && idx.TryGetValue(htr, out var hix) ? hix : -1;
                        if (hand && handI < 0) continue;
                        var ts = new TwistSet { Bend = bi, To = to[bi], HandMode = hand, Hand = handI, Bones = tw.ToArray(), F = new float[tw.Count] };
                        for (int t = 0; t < tw.Count; t++) { ts.F[t] = hand ? (t + 1f) / (tw.Count + 1f) : (t + 1f) / tw.Count; _kind[tw[t]] = Twist; twists++; }
                        _twists.Add(ts);
                    }
                // IK chains
                foreach (var side in new[] { "L", "R" })
                {
                    int[] Chain(string a, string b, string c)
                    {
                        if (!am.TryGetValue(K(a, side), out var ta) || !am.TryGetValue(K(b, side), out var tb) || !am.TryGetValue(K(c, side), out var tc)) return null;
                        if (!idx.ContainsKey(ta) || !idx.ContainsKey(tb) || !idx.ContainsKey(tc)) return null;
                        int ia = idx[ta], ib = idx[tb], ic = idx[tc];
                        if (_her[ia] < 0 || _her[ib] < 0 || _her[ic] < 0) return null;
                        return new[] { ia, ib, ic, _her[ia], _her[ib], _her[ic] };
                    }
                    var arm = Chain("upperarm", "forearm", "hand"); if (arm != null) _arms.Add(arm);
                    var leg = Chain("thigh", "shin", "foot"); if (leg != null) _legs.Add(leg);
                }

                // aligned rest, parents first
                for (int i = 0; i < n; i++)
                {
                    int p = _p[i];
                    var carry = p >= 0 ? _al[p] * Quaternion.Inverse(_g[p]) : Quaternion.identity;
                    var kq = carry * _g[i];
                    var c = Quaternion.identity;
                    if (to[i] >= 0)
                    {
                        var da = carry * (_h[to[i]] - _h[i]); var dh = _hp[toHer[i]] - _hp[_her[i]];
                        c = Quaternion.FromToRotation(da, dh);
                        if (across[i] != null)
                        {
                            var u = dh.normalized;
                            var xa = c * (carry * (_h[across[i][1]] - _h[across[i][0]])); var xh = _hp[across[i][3]] - _hp[across[i][2]];
                            xa -= Vector3.Dot(xa, u) * u; xh -= Vector3.Dot(xh, u) * u;
                            if (xa.sqrMagnitude > 1e-10f && xh.sqrMagnitude > 1e-10f) c = Quaternion.FromToRotation(xa, xh) * c;
                        }
                    }
                    _al[i] = c * kq;
                }
                foreach (var ts in _twists) ts.To = to[ts.Bend];
                for (int i = 0; i < n; i++) _write[i] = _kind[i] != Follow;
                _rootOff = _h[_hips] - _hp[_hipsHer];
                _d = new Quaternion[_ht.Count]; _hx = new Vector3[_ht.Count];

                // look: renderers on her layer, materials, the cornea / tear overlays left out (see-through: not in v1)
                var herBody = HerBodyRenderer();
                _herSkin = herBody != null ? herBody.sharedMaterials.FirstOrDefault(m => m != null && m.shader != null && m.shader.name.Contains("Skin")) : null;
                foreach (var r in _rends)
                {
                    if (herBody != null)
                    {
                        r.gameObject.layer = herBody.gameObject.layer;
                        r.renderingLayerMask = herBody.renderingLayerMask;
                        r.shadowCastingMode = herBody.shadowCastingMode;
                        r.receiveShadows = herBody.receiveShadows;
                        r.lightProbeUsage = herBody.lightProbeUsage;
                        r.reflectionProbeUsage = herBody.reflectionProbeUsage;
                    }
                    r.updateWhenOffscreen = true;
                    r.skinnedMotionVectors = true;
                }
                Materials(MaterialRoute.Value, Brightness.Value);
                Hide(KeepHair.Value);

                int verts = _rends.Sum(r => r.sharedMesh != null ? r.sharedMesh.vertexCount : 0);
                Summary = Name + ": " + best.Id + " rig, " + mapped + " bones mapped (" + spread + " spread, " + twists + " twist), x" + scale.ToString("0.000", CultureInfo.InvariantCulture) +
                          ", " + _rends.Length + " meshes, " + verts + " vertices, " + _hidden.Count + " of her renderers hidden";
                Plugin.Log.LogInfo("Avatar " + Summary + "; facing turned " + yaw.ToString("0.#", CultureInfo.InvariantCulture) + " deg; her skin material " +
                                   (_herSkin != null ? _herSkin.name + " (" + _herSkin.shader.name + ")" : "not found") + "; bundle " + e.Bundle);
                return null;
            }

            static float P(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }
            static float Mean(Func<string, Vector3> pos, string key) { return 0.5f * (pos(key + ".L").y + pos(key + ".R").y); }

            void ReadRest()
            {
                if (_t == null) return;
                var inv = Quaternion.Inverse(HerRoot.rotation);
                for (int i = 0; i < _t.Length; i++)
                {
                    _g[i] = inv * _t[i].rotation;
                    _h[i] = HerRoot.InverseTransformPoint(_t[i].position);
                }
            }

            Dictionary<Transform, KeyValuePair<Vector3, Quaternion>> HerBind()
            {
                var cand = new Dictionary<Transform, List<KeyValuePair<Matrix4x4, int>>>();
                var w2l = HerRoot.worldToLocalMatrix;
                foreach (var r in HerRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (r == null || r.sharedMesh == null || VrHands.UnderSf(r.transform)) continue;
                    var bones = r.bones; var bp = r.sharedMesh.bindposes;
                    var mr = w2l * r.transform.localToWorldMatrix;
                    for (int i = 0; i < bones.Length && i < bp.Length; i++)
                    {
                        if (bones[i] == null) continue;
                        var m = mr * bp[i].inverse;
                        if (!cand.TryGetValue(bones[i], out var l)) cand[bones[i]] = l = new List<KeyValuePair<Matrix4x4, int>>();
                        int at = l.FindIndex(x => Close(x.Key, m));
                        if (at >= 0) l[at] = new KeyValuePair<Matrix4x4, int>(l[at].Key, l[at].Value + 1); else l.Add(new KeyValuePair<Matrix4x4, int>(m, 1));
                    }
                }
                var outp = new Dictionary<Transform, KeyValuePair<Vector3, Quaternion>>();
                foreach (var kv in cand) { var b = kv.Value.OrderByDescending(x => x.Value).First().Key; outp[kv.Key] = new KeyValuePair<Vector3, Quaternion>(b.GetColumn(3), b.rotation); }
                foreach (var kv in cand)
                {
                    if (kv.Value.Count < 2) continue;
                    string nm = kv.Key.name;
                    if (!(nm.EndsWith("_L", StringComparison.Ordinal) || nm.EndsWith("_R", StringComparison.Ordinal))) continue;
                    string o = nm.Substring(0, nm.Length - 2) + (nm.EndsWith("_L", StringComparison.Ordinal) ? "_R" : "_L");
                    var ot = cand.Keys.FirstOrDefault(t => t.name == o);
                    if (ot == null || cand[ot].Count != 1) continue;
                    Vector3 mp = cand[ot][0].Key.GetColumn(3); mp.x = -mp.x;
                    var b = kv.Value.OrderBy(x => ((Vector3)x.Key.GetColumn(3) - mp).sqrMagnitude).First().Key;
                    outp[kv.Key] = new KeyValuePair<Vector3, Quaternion>(b.GetColumn(3), b.rotation);
                }
                return outp;
            }

            static bool Close(Matrix4x4 a, Matrix4x4 b)
            {
                for (int i = 0; i < 16; i++) if (Mathf.Abs(a[i] - b[i]) > 1e-4f) return false;
                return true;
            }

            SkinnedMeshRenderer HerBodyRenderer()
            {
                SkinnedMeshRenderer best = null;
                foreach (var r in HerRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (r == null || r.sharedMesh == null || VrHands.UnderSf(r.transform)) continue;
                    bool skin = r.sharedMaterials.Any(m => m != null && m.shader != null && m.shader.name.Contains("Skin"));
                    if (!skin) continue;
                    if (best == null || r.name.Contains("upperBody") || (!best.name.Contains("upperBody") && r.sharedMesh.vertexCount > best.sharedMesh.vertexCount)) best = r;
                    if (r.name.Contains("upperBody")) break;
                }
                return best;
            }

            // ---- her renderers off (hair optionally kept), given back exactly ----
            public void Hide(bool keepHair)
            {
                Hair = keepHair;
                if (HerRoot == null) return;
                foreach (var r in HerRoot.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || VrHands.UnderSf(r.transform)) continue;
                    var n = r.name.ToLowerInvariant();
                    bool hair = n.Contains("hair") && !n.Contains("pubic");
                    if (hair && keepHair)
                    {
                        if (_hidden.TryGetValue(r, out var was)) { r.forceRenderingOff = was; _hidden.Remove(r); }
                        continue;
                    }
                    if (_hidden.ContainsKey(r)) { r.forceRenderingOff = true; continue; }
                    _hidden[r] = r.forceRenderingOff;
                    r.forceRenderingOff = true;
                }
            }

            // ---- materials: from the game's own loaded shaders ----
            static Type _hdMaterial; static MethodInfo _validate; static bool _looked;

            static void Validate(Material m)
            {
                if (!_looked)
                {
                    _looked = true;
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        _hdMaterial = a.GetType("UnityEngine.Rendering.HighDefinition.HDMaterial", false);
                        if (_hdMaterial != null) break;
                    }
                    _validate = _hdMaterial != null ? _hdMaterial.GetMethod("ValidateMaterial", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Material) }, null) : null;
                    Plugin.Log.LogInfo("Avatar: HDMaterial.ValidateMaterial " + (_validate != null ? "found" : "not found (keywords set by hand)"));
                }
                if (_validate != null) try { _validate.Invoke(null, new object[] { m }); } catch (Exception e) { Plugin.Log.LogWarning("Avatar: ValidateMaterial: " + e.Message); }
            }

            Texture2D Tex(Dictionary<string, string> t, string kind)
            {
                return t != null && t.TryGetValue(kind, out var n) ? _bundle.LoadAsset<Texture2D>(n) : null;
            }

            static Material LitTemplate()
            {
                Material best = null;
                foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (m == null || m.shader == null || m.shader.name != "HDRP/Lit") continue;
                    if (m.HasProperty("_SurfaceType") && m.GetFloat("_SurfaceType") != 0f) continue;
                    best = m;
                    if (m.HasProperty("_MaterialID") && m.GetFloat("_MaterialID") == 1f) break;
                }
                if (best != null) return best;
                var s = Shader.Find("HDRP/Lit");
                return s != null ? new Material(s) : null;
            }

            public void Materials(int route, float bright)
            {
                foreach (var m in _made) if (m != null) UnityEngine.Object.Destroy(m);
                _made.Clear();
                foreach (var m in _meshes) if (m != null) UnityEngine.Object.Destroy(m);
                _meshes.Clear();
                Route = route; Bright = bright;
                var lit = LitTemplate();
                var report = new List<string>();
                for (int ri = 0; ri < _rends.Length; ri++)
                {
                    var r = _rends[ri];
                    var mats = (Material[])_origMats[ri].Clone();
                    var mesh = _origMesh[ri];
                    Mesh copy = null;

                    for (int s = 0; s < mats.Length; s++)
                    {
                        string surf = mats[s] != null ? mats[s].name.Replace(" (Instance)", "") : "";
                        string role = _surfRole.TryGetValue(surf, out var ro) ? ro : "skin";
                        _surfTex.TryGetValue(surf, out var tex);
                        var col = _surfCol.TryGetValue(surf, out var cc) ? cc : Color.white;
                        Material m = null;
                        if (role == "cornea" || role == "tear" || ((role == "lashes" || role == "brows") && lit == null))
                        {
                            // see-through overlays (and cut-outs without a Lit shader) are left out in v1: an empty sub-mesh
                            if (copy == null && mesh != null && mesh.isReadable) { copy = UnityEngine.Object.Instantiate(mesh); copy.name = mesh.name; _meshes.Add(copy); }
                            if (copy != null && s < copy.subMeshCount) copy.SetIndices(new int[0], MeshTopology.Triangles, s, false);
                            report.Add(surf + " left out");
                            m = mats[s];
                        }
                        else if ((role == "skin" || role == "nails") && route == 0 && _herSkin != null) m = SkinCopy(tex, bright);
                        else if (lit != null) m = LitCopy(lit, role, tex, col, bright);
                        else if (_herSkin != null) m = SkinCopy(tex, bright);
                        if (m != null && m != mats[s]) { m.name = "SfAvatar " + surf; _made.Add(m); mats[s] = m; }
                    }
                    r.sharedMaterials = mats;
                    r.sharedMesh = copy ?? mesh;
                }
                Plugin.Log.LogInfo("Avatar materials: " + (route == 0 ? "her skin shader" : "HDRP/Lit") + " for the skin, Lit " + (lit != null ? "from " + lit.name : "missing") +
                                   ", brightness " + bright.ToString("0.00", CultureInfo.InvariantCulture) + (report.Count > 0 ? "; " + string.Join(", ", report.ToArray()) : ""));
            }

            Material SkinCopy(Dictionary<string, string> tex, float bright)
            {
                var m = new Material(_herSkin);
                void T(string p, Texture v) { if (m.HasProperty(p)) m.SetTexture(p, v); }
                void F(string p, float v) { if (m.HasProperty(p)) m.SetFloat(p, v); }
                T("_ColorMap", Tex(tex, "albedo"));
                T("_NormalMap", Tex(tex, "normal")); F("_NormalPower", 1f);
                T("_NormalMap_2", null); F("_NormalPower_2", 0f);
                var sm = Tex(tex, "smooth");
                T("_SmoothnessMap", sm); F("_SmoothnessPower", sm != null ? 1f : 0.4f);
                T("_AddMap", null); F("_AddPower", 0f);
                T("_BlushMap", null); F("_BlushPower", 0f);
                for (int i = 0; i < 3; i++)
                {
                    T("_AttachMap_" + i, null);
                    if (m.HasProperty("_AttachColor_" + i)) { var c = m.GetColor("_AttachColor_" + i); c.a = 0f; m.SetColor("_AttachColor_" + i, c); }
                }
                T("_SkinColorMaskMap", null);
                if (m.HasProperty("_SkinColor")) m.SetColor("_SkinColor", new Color(bright, bright, bright, 1f));
                F("_CutoutSize", 0f);
                return m;
            }

            Material LitCopy(Material tpl, string role, Dictionary<string, string> tex, Color col, float bright)
            {
                var m = new Material(tpl);
                bool cut = role == "lashes" || role == "brows";
                bool sss = (role == "skin" || role == "mouth") && _herSkin != null && _herSkin.HasProperty("_DiffusionProfileHash");
                var albedo = cut ? Tex(tex, "albedoalpha") ?? Tex(tex, "albedo") : Tex(tex, "albedo");
                var normal = Tex(tex, "normal");
                var mask = Tex(tex, "mask");
                float b = role == "skin" || role == "nails" || role == "mouth" ? bright : 1f;
                m.SetTexture("_BaseColorMap", albedo);
                m.SetColor("_BaseColor", albedo != null ? new Color(b, b, b, 1f) : new Color(col.r * b, col.g * b, col.b * b, 1f));
                m.SetFloat("_Metallic", 0f);
                m.SetTexture("_NormalMap", normal); m.SetFloat("_NormalScale", 1f);
                if (normal != null) { m.EnableKeyword("_NORMALMAP"); m.EnableKeyword("_NORMALMAP_TANGENT_SPACE"); } else m.DisableKeyword("_NORMALMAP");
                m.SetTexture("_MaskMap", mask);
                if (mask != null) { m.EnableKeyword("_MASKMAP"); m.SetFloat("_SmoothnessRemapMin", 0f); m.SetFloat("_SmoothnessRemapMax", 1f); m.SetFloat("_AORemapMin", 0f); m.SetFloat("_AORemapMax", 1f); }
                else { m.DisableKeyword("_MASKMAP"); m.SetFloat("_Smoothness", role == "eye" ? 0.85f : role == "teeth" ? 0.7f : role == "mouth" ? 0.6f : 0.4f); }
                foreach (var t in new[] { "_DetailMap", "_HeightMap", "_BentNormalMap", "_EmissiveColorMap", "_SubsurfaceMaskMap", "_ThicknessMap", "_TransmissionMaskMap" })
                    if (m.HasProperty(t)) m.SetTexture(t, null);
                if (sss)
                {
                    m.SetFloat("_MaterialID", 0f);   // subsurface scattering, with her skin's diffusion profile
                    m.SetFloat("_DiffusionProfileHash", _herSkin.GetFloat("_DiffusionProfileHash"));
                    if (_herSkin.HasProperty("_DiffusionProfileAsset")) m.SetVector("_DiffusionProfileAsset", _herSkin.GetVector("_DiffusionProfileAsset"));
                    m.SetFloat("_SubsurfaceMask", 1f); m.SetFloat("_TransmissionEnable", 0f);
                    m.EnableKeyword("_MATERIAL_FEATURE_SUBSURFACE_SCATTERING"); m.DisableKeyword("_MATERIAL_FEATURE_TRANSMISSION");
                    foreach (var p in new[] { "_StencilRef", "_StencilWriteMask", "_StencilRefGBuffer", "_StencilWriteMaskGBuffer", "_StencilRefDepth", "_StencilWriteMaskDepth", "_StencilRefMV", "_StencilWriteMaskMV", "_RequireSplitLighting" })
                        if (_herSkin.HasProperty(p) && m.HasProperty(p)) m.SetFloat(p, _herSkin.GetFloat(p));
                }
                else
                {
                    m.SetFloat("_MaterialID", 1f);
                    m.DisableKeyword("_MATERIAL_FEATURE_SUBSURFACE_SCATTERING"); m.DisableKeyword("_MATERIAL_FEATURE_TRANSMISSION");
                }
                if (cut)
                {
                    m.SetFloat("_AlphaCutoffEnable", 1f); m.SetFloat("_AlphaCutoff", 0.5f);
                    foreach (var p in new[] { "_AlphaCutoffShadow", "_AlphaCutoffPrepass", "_AlphaCutoffPostpass" }) if (m.HasProperty(p)) m.SetFloat(p, 0.5f);
                    m.EnableKeyword("_ALPHATEST_ON");
                    m.SetFloat("_DoubleSidedEnable", 1f); m.EnableKeyword("_DOUBLESIDED_ON");
                    foreach (var p in new[] { "_CullMode", "_CullModeForward" }) if (m.HasProperty(p)) m.SetFloat(p, 0f);
                    m.renderQueue = (int)RenderQueue.AlphaTest;
                }
                else
                {
                    m.SetFloat("_AlphaCutoffEnable", 0f); m.DisableKeyword("_ALPHATEST_ON");
                    m.renderQueue = (int)RenderQueue.Geometry;
                }
                Validate(m);
                return m;
            }

            // ---- every frame ----
            public void Drive()
            {
                if (HerRoot == null || Go == null) return;
                Go.transform.SetPositionAndRotation(HerRoot.position, HerRoot.rotation);
                var inv = Quaternion.Inverse(HerRoot.rotation);
                for (int h = 0; h < _ht.Count; h++)
                {
                    var t = _ht[h];
                    if (t == null) { _d[h] = Quaternion.identity; continue; }
                    _d[h] = inv * t.rotation * Quaternion.Inverse(_hb[h]);
                    _hx[h] = HerRoot.InverseTransformPoint(t.position);
                }
                int n = _t.Length;
                for (int i = 0; i < n; i++)
                {
                    _fixed[i] = false;
                    if (_kind[i] == Mapped) { _w[i] = _d[_her[i]] * _al[i]; _fixed[i] = true; }
                    else if (_kind[i] == Spread) { var lo = _d[_lo[i]]; _w[i] = lo * Pow(Quaternion.Inverse(lo) * _d[_hi[i]], _f[i]) * _al[i]; _fixed[i] = true; }
                }
                var rootPos = _hx[_hipsHer] + _d[_hipsHer] * _rootOff;
                Resolve(rootPos);
                float hik = HandIk.Value, fik = FootIk.Value;
                if (hik > 0f) foreach (var c in _arms) TwoBone(c, hik);
                if (fik > 0f) foreach (var c in _legs) TwoBone(c, fik);
                if (hik > 0f || fik > 0f) Resolve(rootPos);
                foreach (var ts in _twists) TwistSplit(ts);
                if (_twists.Count > 0) Resolve(rootPos);   // bones under a twisted one see its final rotation
                // write: rotations of the bones we drive (the rest keep their rest local rotation), then the hips' position
                var hr = HerRoot.rotation;
                for (int i = 0; i < n; i++)
                {
                    if (!_write[i] || !_fixed[i] || _t[i] == null) continue;
                    if (_p[i] < 0) _t[i].rotation = hr * _w[i];
                    else _t[i].localRotation = Quaternion.Inverse(_w[_p[i]]) * _w[i];
                }
                if (_t[_hips] != null) _t[_hips].position = HerRoot.TransformPoint(_x[_hips]);
            }

            void Resolve(Vector3 rootPos)
            {
                int n = _t.Length;
                for (int i = 0; i < n; i++)
                {
                    int p = _p[i];
                    if (!_fixed[i]) _w[i] = p >= 0 ? _w[p] * Quaternion.Inverse(_al[p]) * _al[i] : _al[i];
                    _x[i] = p >= 0 ? _x[p] + _w[p] * (Quaternion.Inverse(_g[p]) * (_h[i] - _h[p])) : _h[i];
                }
                var d = rootPos - _x[_hips];
                for (int i = 0; i < n; i++) _x[i] += d;
            }

            // two-bone IK toward her end joint (by weight), pole at her middle joint; the end keeps its rotation
            void TwoBone(int[] c, float wgt)
            {
                int a = c[0], m = c[1], e = c[2];
                Vector3 pa = _x[a], pm = _x[m], pe = _x[e];
                Vector3 target = Vector3.Lerp(pe, _hx[c[5]], wgt), pole = _hx[c[4]];
                float la = (pm - pa).magnitude, lb = (pe - pm).magnitude;
                var tv = target - pa;
                float dist = Mathf.Clamp(tv.magnitude, Mathf.Abs(la - lb) + 1e-4f, la + lb - 1e-4f);
                if (tv.sqrMagnitude < 1e-10f) return;
                var tn = tv.normalized;
                float x = (la * la - lb * lb + dist * dist) / (2f * dist);
                float hgt = Mathf.Sqrt(Mathf.Max(la * la - x * x, 0f));
                var pv = pole - pa; pv -= Vector3.Dot(pv, tn) * tn;
                if (pv.sqrMagnitude < 1e-10f) { pv = pm - pa; pv -= Vector3.Dot(pv, tn) * tn; }
                if (pv.sqrMagnitude < 1e-10f) return;
                pv.Normalize();
                var mid = pa + tn * x + pv * hgt;
                var end = pa + tn * dist;
                var r1 = Quaternion.FromToRotation(pm - pa, mid - pa);
                var r2 = Quaternion.FromToRotation(r1 * (pe - pm), end - mid);
                _w[a] = r1 * _w[a];
                _w[m] = r2 * r1 * _w[m];
            }

            void TwistSplit(TwistSet ts)
            {
                int b = ts.Bend, p = _p[b];
                if (ts.To < 0) return;
                var dA = _al[b] * (Quaternion.Inverse(_g[b]) * (_h[ts.To] - _h[b]));   // the segment at the aligned rest
                if (!ts.HandMode)
                {
                    if (p < 0) return;
                    var carry = _w[p] * Quaternion.Inverse(_al[p]);
                    var delta = _w[b] * Quaternion.Inverse(carry * _al[b]);
                    var tw = TwistAbout(delta, carry * dA);
                    var swing = delta * Quaternion.Inverse(tw);
                    _w[b] = swing * carry * _al[b];
                    for (int k = 0; k < ts.Bones.Length; k++) { int t = ts.Bones[k]; _w[t] = swing * Pow(tw, ts.F[k]) * carry * _al[t]; _fixed[t] = true; }
                }
                else
                {
                    var carry = _w[b] * Quaternion.Inverse(_al[b]);
                    var delta = _w[ts.Hand] * Quaternion.Inverse(carry * _al[ts.Hand]);
                    var tw = TwistAbout(delta, carry * dA);
                    for (int k = 0; k < ts.Bones.Length; k++) { int t = ts.Bones[k]; _w[t] = Pow(tw, ts.F[k]) * carry * _al[t]; _fixed[t] = true; }
                }
            }

            public void Destroy()
            {
                foreach (var kv in _hidden) if (kv.Key != null) kv.Key.forceRenderingOff = kv.Value;
                _hidden.Clear();
                if (Go != null) UnityEngine.Object.Destroy(Go);
                foreach (var m in _made) if (m != null) UnityEngine.Object.Destroy(m);
                _made.Clear();
                foreach (var m in _meshes) if (m != null) UnityEngine.Object.Destroy(m);
                _meshes.Clear();
                if (_bundle != null) { _bundle.Unload(true); _bundle = null; }
            }
        }
    }
}
