using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace SfMorph
{
    // Avatar replacement (docs/SOURCES.md, 'A third mode: avatar replacement'): a character's own mesh and textures from an avatar
    // bundle (tools/avatar/build.ps1) drawn instead of her, skinned to HER skeleton.
    //
    // 0.11.29 drove the avatar's own skeleton from her pose every frame: the pose matched, but nothing of hers carried over (the
    // game's breast physics, her hair on her head, her feet on the floor; the avatar's own limb lengths made its feet drift and
    // dip through the floor and its body move off her curves). Since 0.11.30 the avatar mesh is rebuilt once, at load, onto her
    // skeleton, and the game skins it with her bones like her own meshes:
    //   1. pose: the avatar's rest turned bone by bone into her bind pose (each limb / finger segment along hers, the hand by two
    //      directions; trunk, collars, eyes and jaw keep their rest: their directions are rig conventions);
    //   2. fit: a smooth warp (biharmonic RBF with an affine part) moves the avatar's joints onto hers: head, eyes, shoulders,
    //      elbows, wrists, finger joints, hips, knees, ankles, toes (no spine / collar joints: rig conventions again), so every
    //      limb bends at her pivots and the feet stand where hers do;
    //   3. weights: torso, breasts, glutes, arms and legs take her own skin weights from the nearest vertex of her body meshes
    //      (her breast chain moved by the game's DynamicBone, her twist bones, her skin helpers), head / hands / feet and anything
    //      farther than 4 cm from her skin by the bone map (the avatar's bone -> hers; its extra spine / neck bones split between
    //      her neighbours; its twist bones -> her ShoulderPart / ElbowPart / HipPart);
    //   4. the new meshes are bound to her bones with her bind poses; her renderers are hidden (forceRenderingOff), her hair
    //      optionally kept. No per-frame work: her animation, IK, DynamicBone, Overlap / Embody and the Body / Bones sliders on
    //      her bones move the avatar exactly as they move her.
    internal static class Avatar
    {
        public static ConfigEntry<string> Pick;
        public static ConfigEntry<int> MaterialRoute;
        public static ConfigEntry<float> Brightness;
        public static ConfigEntry<bool> KeepHair;

        public static void Bind(ConfigFile cfg)
        {
            Pick = cfg.Bind("Avatar", "Name", "", "Avatar replacement: the avatar drawn instead of her (a folder under BepInEx\\config\\SfMorphs\\avatars made by tools/avatar/build.ps1). Empty: off.");
            // 0.11.41: a new key, so the saved 0.11.29-0.11.39 value (HDRP/Lit, which looked like a ghost: user) does not carry over
            MaterialRoute = cfg.Bind("Avatar", "SkinShader", 0, new ConfigDescription("Avatar skin shader: 0 a copy of her own skin material (Shader Graphs/Skin: the game's skin shading and subsurface scattering, her makeup / blush layers off) with the avatar's textures, 1 the game's HDRP/Lit with subsurface scattering and her skin's diffusion profile.", new AcceptableValueRange<int>(0, 1)));
            Brightness = cfg.Bind("Avatar", "Brightness", 1f, new ConfigDescription("Avatar skin brightness (multiplies its colour maps).", new AcceptableValueRange<float>(0.5f, 1.5f)));
            KeepHair = cfg.Bind("Avatar", "KeepHair", true, "Keep her hair on while the avatar is drawn (the avatar's head is fitted onto hers, so her hair sits on it).");
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

        // 0.11.42: the avatar is her body for the plugin's skin systems (Body.Scan puts its renderers in her Character.Renderers and her
        // hidden skin renderers out): GPU contact (dents, Delta Mush, chubby, bends, ripples), skin marks, soft zones, her body colliders
        internal static bool Owns(Renderer r) { return _rig != null && _rig.Go != null && r != null && r.transform.IsChildOf(_rig.Go.transform); }
        internal static bool Hides(Renderer r) { return _rig != null && r != null && _rig.HidesR(r); }
        internal static Transform HerRoot { get { return _rig != null ? _rig.HerRoot : null; } }

        // Wrinkles' region classes for the avatar's materials: 1 its head skin, 3 the rest of its skin (body, arms, legs), 0 not skin
        internal static int RegionOf(Material m, Renderer r)
        {
            if (_rig == null || m == null) return 0;
            return _rig.RegionOf(m);
        }

        // From Runner.Update: loads / unloads by the config; every 2 s her renderers the game added since are hidden too.
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
                if (_rig != null && (_rig.HerRoot != her.Root || _rig.Name != want || _rig.Go == null || _rig.Broken))
                    Unload(_rig.Go == null ? "its object went away (scene change)" : _rig.HerRoot != her.Root ? "her character changed" : _rig.Broken ? "her skeleton changed" : "another avatar picked");
                if (_rig == null)
                {
                    if (_failed == want && _failedRoot == her.Root) return;
                    Load(want, her);
                    if (_rig == null) { _failed = want; _failedRoot = her.Root; }
                    else _failed = null;
                    return;
                }
                if (_rig.Route != MaterialRoute.Value) _rig.Materials(MaterialRoute.Value, Brightness.Value);
                else if (Mathf.Abs(_rig.Bright - Brightness.Value) > 0.001f) _rig.SetBrightness(Brightness.Value);
                if (_rig.Hair != KeepHair.Value) _rig.Hide(KeepHair.Value);
                if (Time.unscaledTime >= _nextCheck)
                {
                    _nextCheck = Time.unscaledTime + 2f;
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
            string err;
            try { err = rig.Build(e, her); }
            catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; Plugin.Log.LogError("Avatar build: " + ex); }
            if (err != null)
            {
                Status = "off: " + err;
                Plugin.Log.LogWarning("Avatar '" + name + "': " + err);
                rig.Destroy();
                return;
            }
            _rig = rig;
            Status = rig.Summary;
        }

        static void Unload(string why)
        {
            if (_rig == null) return;
            Plugin.Log.LogInfo("Avatar '" + _rig.Name + "' off: " + why);
            var r = _rig; _rig = null;
            r.Destroy();
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
        // Face landmarks for the face weights (0.11.43): lip corners, the middle of the upper and lower lip, inner and outer brows. Hers
        // (AdvancedSkeleton face rig) and per avatar rig family; a rig without them (no face bones) keeps the jaw / eyes / head weights.
        static readonly string[] FaceKeys = { "lipcorner.L", "lipcorner.R", "lipupper", "liplower", "browin.L", "browin.R", "browout.L", "browout.R" };
        static readonly Dictionary<string, string[]> HerFace = new Dictionary<string, string[]>
        {
            { "lipcorner.L", new[] { "Lip_Left_Inside" } }, { "lipcorner.R", new[] { "Lip_Right_Inside" } },
            { "lipupper", new[] { "Lip_Upper_Inside" } }, { "liplower", new[] { "Lip_Lower_Insite", "Lip_Lower_Inside" } },
            { "browin.L", new[] { "Eyebrow_Left_Insite" } }, { "browin.R", new[] { "Eyebrow_Right_Insite" } },
            { "browout.L", new[] { "Eyebrow_Left_Outsite" } }, { "browout.R", new[] { "Eyebrow_Right_Outsite" } },
        };
        static readonly Dictionary<string, Dictionary<string, string[]>> AvatarFace = new Dictionary<string, Dictionary<string, string[]>>
        {
            { "g9", new Dictionary<string, string[]> {
                { "lipcorner.L", new[] { "l_lipcorner" } }, { "lipcorner.R", new[] { "r_lipcorner" } },
                { "lipupper", new[] { "lipuppermiddle" } }, { "liplower", new[] { "liplowermiddle" } },
                { "browin.L", new[] { "l_browinner" } }, { "browin.R", new[] { "r_browinner" } },
                { "browout.L", new[] { "l_browouter" } }, { "browout.R", new[] { "r_browouter" } } } },
            { "g8", new Dictionary<string, string[]> {
                { "lipcorner.L", new[] { "lLipCorner" } }, { "lipcorner.R", new[] { "rLipCorner" } },
                { "lipupper", new[] { "LipUpperMiddle", "lipUpperMiddle" } }, { "liplower", new[] { "LipLowerMiddle", "lipLowerMiddle" } },
                { "browin.L", new[] { "lBrowInner" } }, { "browin.R", new[] { "rBrowInner" } },
                { "browout.L", new[] { "lBrowOuter" } }, { "browout.R", new[] { "rBrowOuter" } } } },
            { "g2", new Dictionary<string, string[]> {
                { "lipcorner.L", new[] { "lLipCorner" } }, { "lipcorner.R", new[] { "rLipCorner" } },
                { "lipupper", new[] { "LipUpperMiddle", "lipUpperMiddle", "LipUpper" } }, { "liplower", new[] { "LipLowerMiddle", "lipLowerMiddle", "LipLower" } },
                { "browin.L", new[] { "lBrowInner" } }, { "browin.R", new[] { "rBrowInner" } },
                { "browout.L", new[] { "lBrowOuter" } }, { "browout.R", new[] { "rBrowOuter" } } } },
        };

        static Transform FindAny(Dictionary<string, Transform> byLowerName, string[] names)
        {
            foreach (var n in names) if (byLowerName.TryGetValue(n.ToLowerInvariant(), out var t)) return t;
            return null;
        }

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

        // key (+ '.L' / '.R' for sided keys) -> bone, from a name lookup (lower case)
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

        // ---- a smooth warp through point pairs: biharmonic RBF (phi = r) plus an affine part -------------------------------------
        class Rbf
        {
            Vector3[] _c; double[,] _w; int _n;

            public static Rbf Fit(List<Vector3> src, List<Vector3> dst)
            {
                int n = src.Count, m = n + 4;
                var a = new double[m, m + 3];
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++) a[i, j] = (src[i] - src[j]).magnitude + (i == j ? 1e-6 : 0.0);
                    a[i, n] = src[i].x; a[i, n + 1] = src[i].y; a[i, n + 2] = src[i].z; a[i, n + 3] = 1.0;
                    a[n, i] = src[i].x; a[n + 1, i] = src[i].y; a[n + 2, i] = src[i].z; a[n + 3, i] = 1.0;
                    var d = dst[i] - src[i];
                    a[i, m] = d.x; a[i, m + 1] = d.y; a[i, m + 2] = d.z;
                }
                // Gaussian elimination with partial pivoting, three right-hand sides
                for (int col = 0; col < m; col++)
                {
                    int piv = col;
                    for (int r = col + 1; r < m; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
                    if (Math.Abs(a[piv, col]) < 1e-12) continue;
                    if (piv != col) for (int c = 0; c < m + 3; c++) { var t = a[col, c]; a[col, c] = a[piv, c]; a[piv, c] = t; }
                    for (int r = 0; r < m; r++)
                    {
                        if (r == col) continue;
                        double f = a[r, col] / a[col, col];
                        if (f == 0.0) continue;
                        for (int c = col; c < m + 3; c++) a[r, c] -= f * a[col, c];
                    }
                }
                var w = new double[m, 3];
                for (int r = 0; r < m; r++) for (int k = 0; k < 3; k++) w[r, k] = Math.Abs(a[r, r]) > 1e-12 ? a[r, m + k] / a[r, r] : 0.0;
                return new Rbf { _c = src.ToArray(), _w = w, _n = n };
            }

            public Vector3 Apply(Vector3 p)
            {
                double x = 0, y = 0, z = 0;
                for (int i = 0; i < _n; i++)
                {
                    double r = (p - _c[i]).magnitude;
                    x += _w[i, 0] * r; y += _w[i, 1] * r; z += _w[i, 2] * r;
                }
                for (int k = 0; k < 3; k++)
                {
                    double v = _w[_n, k] * p.x + _w[_n + 1, k] * p.y + _w[_n + 2, k] * p.z + _w[_n + 3, k];
                    if (k == 0) x += v; else if (k == 1) y += v; else z += v;
                }
                return p + new Vector3((float)x, (float)y, (float)z);
            }
        }

        // ---- one loaded avatar ------------------------------------------------------------------------------------------------
        class Rig
        {
            public string Name, Summary = "";
            public Transform HerRoot;
            public GameObject Go;
            public int Route = -1;
            public float Bright = -1f;
            public bool Hair = true;
            AssetBundle _bundle;
            readonly List<Material> _made = new List<Material>();
            readonly List<KeyValuePair<Material, Color>> _tint = new List<KeyValuePair<Material, Color>>();   // material, its colour at brightness 1
            readonly List<Mesh> _meshes = new List<Mesh>();
            readonly Dictionary<Renderer, bool> _hidden = new Dictionary<Renderer, bool>();
            readonly List<SkinnedMeshRenderer> _rends = new List<SkinnedMeshRenderer>();
            readonly List<Material[]> _origMats = new List<Material[]>();   // the bundle's materials per new renderer (named by surface)
            readonly Dictionary<string, string> _surfRole = new Dictionary<string, string>();
            readonly Dictionary<string, Dictionary<string, string>> _surfTex = new Dictionary<string, Dictionary<string, string>>();
            readonly Dictionary<string, Color> _surfCol = new Dictionary<string, Color>();
            Material _herSkin;
            Transform[] _herBones;   // the bones the new meshes are bound to

            internal bool HidesR(Renderer r) { return _hidden.ContainsKey(r); }

            internal int RegionOf(Material m)
            {
                string n = m.name ?? "";
                if (n.StartsWith("SfAvatar ", StringComparison.Ordinal)) n = n.Substring(9);
                if (!_surfRole.TryGetValue(n, out var role) || role != "skin") return 0;
                var l = n.ToLowerInvariant();
                if (l.Contains("mouth")) return 0;   // the mouth cavity
                return l.Contains("head") || l.Contains("face") || l.Contains("lip") || l.Contains("ear") || l.Contains("nostril") || l.Contains("eyesocket") ? 1 : 3;
            }

            // a bone of hers the new meshes use went away (the game rebuilt her): reload
            public bool Broken { get { if (_herBones == null) return false; foreach (var b in _herBones) if (b == null) return true; return false; } }

            public string Build(Entry e, Character her)
            {
                Name = e.Name; HerRoot = her.Root;
                var lines = File.ReadAllLines(Path.Combine(e.Dir, "avatar.txt"));
                _bundle = AssetBundle.LoadFromFile(e.Bundle);
                if (_bundle == null) return "could not load " + e.Bundle + " (built for another Unity version, or already loaded)";
                string prefabName = Val(lines, "prefab") ?? Name;
                var prefab = _bundle.LoadAsset<GameObject>(prefabName);
                if (prefab == null) return "no prefab '" + prefabName + "' in " + e.Bundle;
                ReadSurfaces(lines);
                var tmp = new GameObject("SfAvatarBuild");
                tmp.transform.SetPositionAndRotation(HerRoot.position, HerRoot.rotation);
                try { return BuildFrom(prefab, tmp); }
                finally { UnityEngine.Object.DestroyImmediate(tmp); }
            }

            void ReadSurfaces(string[] lines)
            {
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
            }

            string BuildFrom(GameObject prefab, GameObject tmp)
            {
                var model = UnityEngine.Object.Instantiate(prefab, tmp.transform, false);
                var avRends = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (avRends.Length == 0) return "the prefab has no skinned meshes";
                foreach (var r in avRends) if (r.sharedMesh == null || !r.sharedMesh.isReadable) return "mesh of " + r.name + " is not readable (rebuild the bundle with tools/avatar/build.ps1)";

                // bones: the avatar's by name, hers by name (not ours)
                var avByName = new Dictionary<string, Transform>();
                foreach (var t in model.GetComponentsInChildren<Transform>(true)) { var k = t.name.ToLowerInvariant(); if (!avByName.ContainsKey(k)) avByName[k] = t; }
                var herByName = new Dictionary<string, Transform>();
                foreach (var t in HerRoot.GetComponentsInChildren<Transform>(true)) { if (VrHands.UnderSf(t)) continue; var k = t.name.ToLowerInvariant(); if (!herByName.ContainsKey(k)) herByName[k] = t; }
                var hm = MapBones(Hers, herByName);
                RigTable best = null; Dictionary<string, Transform> am = null;
                foreach (var t in AvatarTables) { var m = MapBones(t, avByName); if (am == null || m.Count > am.Count) { am = m; best = t; } }
                var need = new[] { "hips", "spine", "neck", "head" }.Concat(new[] { "upperarm", "forearm", "hand", "thigh", "shin", "foot" }.SelectMany(k => new[] { k + ".L", k + ".R" })).ToList();
                var missA = need.Where(k => !am.ContainsKey(k)).ToList();
                var missH = need.Where(k => !hm.ContainsKey(k)).ToList();
                if (missA.Count > 0) return "the avatar's rig (" + best.Id + " table) has no " + string.Join(", ", missA.ToArray());
                if (missH.Count > 0) return "her rig has no " + string.Join(", ", missH.ToArray());
                var herBind = HerBind();   // her bone -> bone-to-root at her bind pose
                foreach (var k in need) if (!herBind.ContainsKey(hm[k])) return "no bind pose for her " + hm[k].name;
                Vector3 HP(Transform t) { return herBind[t].GetColumn(3); }

                // the avatar skeleton, parents first
                Transform top = am["hips"];
                while (top.parent != null && top.parent != model.transform && top.parent.GetComponent<Renderer>() == null && top.parent.childCount == 1) top = top.parent;
                var order = new List<Transform>();
                var idx = new Dictionary<Transform, int>();
                void Walk(Transform t) { idx[t] = order.Count; order.Add(t); for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i)); }
                Walk(top);
                int n = order.Count;
                var T = order.ToArray(); var par = new int[n];
                for (int i = 0; i < n; i++) par[i] = T[i].parent != null && idx.TryGetValue(T[i].parent, out var pi) ? pi : -1;
                var G = new Quaternion[n]; var H = new Vector3[n];
                void ReadRest()
                {
                    var inv = Quaternion.Inverse(HerRoot.rotation);
                    for (int i = 0; i < n; i++) { G[i] = inv * T[i].rotation; H[i] = HerRoot.InverseTransformPoint(T[i].position); }
                }
                int I(string key) { return am.TryGetValue(key, out var t) && idx.TryGetValue(t, out var i) ? i : -1; }

                // facing: turn the model about up so its feet point where hers do; size: its legs to hers (the fit does the rest)
                ReadRest();
                Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
                Vector3 avF = I("toe.L") >= 0 && hm.ContainsKey("toe.L") ? Flat(H[I("toe.L")] - H[I("foot.L")]) + Flat(H[I("toe.R")] - H[I("foot.R")]) : Vector3.Cross(H[I("thigh.L")] - H[I("thigh.R")], Vector3.up);
                Vector3 herF = hm.ContainsKey("toe.L") && herBind.ContainsKey(hm["toe.L"]) ? Flat(HP(hm["toe.L"]) - HP(hm["foot.L"])) + Flat(HP(hm["toe.R"]) - HP(hm["foot.R"])) : Vector3.Cross(HP(hm["thigh.L"]) - HP(hm["thigh.R"]), Vector3.up);
                float yaw = avF.sqrMagnitude > 1e-8f && herF.sqrMagnitude > 1e-8f ? Vector3.SignedAngle(avF, herF, Vector3.up) : 0f;
                model.transform.localRotation = Quaternion.AngleAxis(yaw, Vector3.up) * model.transform.localRotation;
                ReadRest();
                float legAv = 0.5f * (H[I("thigh.L")].y + H[I("thigh.R")].y) - 0.5f * (H[I("foot.L")].y + H[I("foot.R")].y);
                float legHer = 0.5f * (HP(hm["thigh.L"]).y + HP(hm["thigh.R"]).y) - 0.5f * (HP(hm["foot.L"]).y + HP(hm["foot.R"]).y);
                float scale = legAv > 1e-4f ? legHer / legAv : 1f;
                if (!(scale > 0.3f && scale < 3f)) scale = 1f;
                tmp.transform.localScale = Vector3.one * scale;
                ReadRest();

                // roles: which of her bones each avatar bone follows, and which segment aligns its rest
                string[] changeOnly = { "hips", "spine", "chest", "neck", "head", "jaw", "eye", "collar" };
                var next = new Dictionary<string, string> { { "upperarm", "forearm" }, { "forearm", "hand" }, { "thigh", "shin" }, { "shin", "foot" }, { "foot", "toe" }, { "hand", "middle1" } };
                var herOf = new Transform[n]; var to = new int[n]; var toHer = new Transform[n]; var across = new int[n][]; var acrossHer = new Transform[n][];
                var keyOf = new string[n];
                for (int i = 0; i < n; i++) to[i] = -1;
                int mapped = 0;
                foreach (var key in Keys)
                    foreach (var side in Array.IndexOf(Keys, key) >= FirstSided ? new[] { "L", "R" } : new string[] { null })
                    {
                        int i = I(K(key, side));
                        if (i < 0 || !hm.TryGetValue(K(key, side), out var ht) || !herBind.ContainsKey(ht)) continue;
                        herOf[i] = ht; keyOf[i] = K(key, side); mapped++;
                        if (Array.IndexOf(changeOnly, key) >= 0) continue;
                        string nk = next.ContainsKey(key) ? next[key] : (key.Length > 1 && char.IsDigit(key[key.Length - 1]) && key[key.Length - 1] < '3') ? key.Substring(0, key.Length - 1) + (char)(key[key.Length - 1] + 1) : null;
                        if (nk != null && I(K(nk, side)) >= 0 && hm.TryGetValue(K(nk, side), out var h2) && herBind.ContainsKey(h2)) { to[i] = I(K(nk, side)); toHer[i] = h2; }
                        if (key == "hand" && I(K("pinky1", side)) >= 0 && I(K("index1", side)) >= 0 && hm.TryGetValue(K("pinky1", side), out var hp1) && hm.TryGetValue(K("index1", side), out var hi1)
                            && herBind.ContainsKey(hp1) && herBind.ContainsKey(hi1))
                        { across[i] = new[] { I(K("pinky1", side)), I(K("index1", side)) }; acrossHer[i] = new[] { hp1, hi1 }; }
                    }

                // 1. pose: the avatar's rest turned into her bind pose (aligned rest), then its joints by forward kinematics
                var A = new Quaternion[n]; var X = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    int p = par[i];
                    var carry = p >= 0 ? A[p] * Quaternion.Inverse(G[p]) : Quaternion.identity;
                    var c = Quaternion.identity;
                    if (to[i] >= 0)
                    {
                        var da = carry * (H[to[i]] - H[i]); var dh = HP(toHer[i]) - HP(herOf[i]);
                        c = Quaternion.FromToRotation(da, dh);
                        if (across[i] != null)
                        {
                            var u = dh.normalized;
                            var xa = c * (carry * (H[across[i][1]] - H[across[i][0]])); var xh = HP(acrossHer[i][1]) - HP(acrossHer[i][0]);
                            xa -= Vector3.Dot(xa, u) * u; xh -= Vector3.Dot(xh, u) * u;
                            if (xa.sqrMagnitude > 1e-10f && xh.sqrMagnitude > 1e-10f) c = Quaternion.FromToRotation(xa, xh) * c;
                        }
                    }
                    A[i] = c * carry * G[i];
                    X[i] = p >= 0 ? X[p] + A[p] * (Quaternion.Inverse(G[p]) * (H[i] - H[p])) : H[i];
                }
                // per avatar bone: rest -> aligned rest (a rigid move in her root's space)
                var Rb = new Quaternion[n]; var tb = new Vector3[n];
                for (int i = 0; i < n; i++) { Rb[i] = A[i] * Quaternion.Inverse(G[i]); tb[i] = X[i] - Rb[i] * H[i]; }

                // 2. the bone map: avatar bone -> her bones (with shares)
                var tgt = new List<KeyValuePair<Transform, float>>[n];
                Transform herRootBone = hm["hips"];
                // trunk bones between mapped ones on the hips -> head path share their neighbours
                var path = new List<int>();
                for (int i = I("head"); i >= 0; i = par[i]) { path.Insert(0, i); if (i == I("hips")) break; }
                var anchors = path.Where(i => herOf[i] != null).ToList();
                for (int a = 0; a + 1 < anchors.Count; a++)
                {
                    int i0 = path.IndexOf(anchors[a]), i1 = path.IndexOf(anchors[a + 1]);
                    for (int j = i0 + 1; j < i1; j++)
                    {
                        float f = (j - i0) / (float)(i1 - i0);
                        tgt[path[j]] = new List<KeyValuePair<Transform, float>> { new KeyValuePair<Transform, float>(herOf[anchors[a]], 1f - f), new KeyValuePair<Transform, float>(herOf[anchors[a + 1]], f) };
                    }
                }
                // twist bones -> her part bones (ShoulderPart / ElbowPart / HipPart 1, 2), in order from the joint
                foreach (var key in new[] { "upperarm", "forearm", "thigh" })
                    foreach (var side in new[] { "L", "R" })
                    {
                        int bi = I(K(key, side));
                        if (bi < 0 || herOf[bi] == null) continue;
                        var tw = new List<int>();
                        for (int c = 0; c < T[bi].childCount; c++)
                        {
                            var ch = T[bi].GetChild(c);
                            if (ch.name.ToLowerInvariant().Contains("twist") && idx.TryGetValue(ch, out var ci) && herOf[ci] == null) tw.Add(ci);
                        }
                        tw.Sort((x, y) => (H[x] - H[bi]).sqrMagnitude.CompareTo((H[y] - H[bi]).sqrMagnitude));
                        string part = key == "upperarm" ? "ShoulderPart" : key == "forearm" ? "ElbowPart" : "HipPart";
                        for (int k = 0; k < tw.Count; k++)
                        {
                            herByName.TryGetValue((part + Mathf.Min(k + 1, 2) + "_" + side).ToLowerInvariant(), out var pt);
                            tgt[tw[k]] = new List<KeyValuePair<Transform, float>> { new KeyValuePair<Transform, float>(pt != null && herBind.ContainsKey(pt) ? pt : herOf[bi], 1f) };
                        }
                    }
                for (int i = 0; i < n; i++)
                {
                    if (herOf[i] != null) tgt[i] = new List<KeyValuePair<Transform, float>> { new KeyValuePair<Transform, float>(herOf[i], 1f) };
                    else if (tgt[i] == null) tgt[i] = par[i] >= 0 ? tgt[par[i]] : new List<KeyValuePair<Transform, float>> { new KeyValuePair<Transform, float>(herRootBone, 1f) };
                }
                // body regions (head, torso, upper arm, forearm, hand, thigh, shin, foot per side) for both rigs: an avatar vertex
                // takes her weights only from her skin of the regions its own weights cover (her hand hangs by her hip in her bind
                // pose, her upper arm by her breast: nearest-vertex alone pulled hip and breast skin along with them)
                var avRegion = new int[n];
                for (int i = 0; i < n; i++)
                {
                    avRegion[i] = 1;
                    for (int a = i; a >= 0; a = par[a]) if (keyOf[a] != null) { avRegion[i] = Region(keyOf[a]); break; }
                }
                var herKey = new Dictionary<Transform, string>();
                foreach (var kv in hm) if (!herKey.ContainsKey(kv.Value)) herKey[kv.Value] = kv.Key;
                var herRegion = new Dictionary<Transform, int>();
                int HerRegion(Transform t)
                {
                    if (t == null) return 1;
                    if (herRegion.TryGetValue(t, out var r0)) return r0;
                    int r = 1;
                    for (var a = t; a != null && a != HerRoot; a = a.parent) if (herKey.TryGetValue(a, out var k)) { r = Region(k); break; }
                    herRegion[t] = r;
                    return r;
                }
                bool ByMap(int region) { return region == 0 || region == 6 || region == 7 || region == 12 || region == 13; }   // head, hands, feet

                // 3. her body skin for the weights: her body meshes at her bind pose, in her root's space
                var near = new SkinGrid(0.03f);
                // her genital patch (the pussy_type* meshes, vulva and anus in one, all variants: they share their rest shape within
                // 1 cm), her pubic hair, and her body surface around them: the avatar's crotch is cut out where the patch sits and the
                // skin around it is laid onto hers, so the game's own genital meshes, their swaps and animations fit it
                var patch = new PointGrid(0.02f); var pubic = new PointGrid(0.02f); var herSurf = new PointGrid(0.02f);
                var herW2L = HerRoot.worldToLocalMatrix;
                string herMeshes = "";
                foreach (var r in HerRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (r == null || r.sharedMesh == null || VrHands.UnderSf(r.transform)) continue;
                    var nm = r.name.ToLowerInvariant();
                    if (IsPatch(nm) || nm.Contains("pubichair"))
                    {
                        var dg = MeshData.Read(r.sharedMesh, out var gerr);
                        if (dg != null && dg.Pos != null) (IsPatch(nm) ? patch : pubic).Add(dg, herW2L * r.transform.localToWorldMatrix);
                        continue;
                    }
                    if (!r.gameObject.activeInHierarchy) continue;
                    if (!(nm.Contains("upperbody") || nm.Contains("lowerbody") || nm.Contains("foot") || nm.Contains("neck"))) continue;
                    if (!r.sharedMaterials.Any(m => m != null && m.shader != null && m.shader.name.Contains("Skin"))) continue;
                    var d = MeshData.Read(r.sharedMesh, out var err);
                    if (d == null || d.Pos == null || d.Weights == null) { Plugin.Log.LogWarning("Avatar: couldn't read " + r.name + ": " + err); continue; }
                    var bones = r.bones;
                    var real = new Transform[bones.Length];
                    for (int b = 0; b < bones.Length; b++)
                    {
                        var t = bones[b];
                        while (t != null && (t.name.StartsWith("Sf", StringComparison.Ordinal) || !herBind.ContainsKey(t))) t = t.parent;   // our soft-zone helpers -> their bone
                        real[b] = t;
                    }
                    near.Add(d, real, herW2L * r.transform.localToWorldMatrix, HerRegion);
                    if (nm.Contains("lowerbody") || nm.Contains("upperbody")) herSurf.Add(d, herW2L * r.transform.localToWorldMatrix);
                    herMeshes += (herMeshes.Length > 0 ? ", " : "") + r.name + " " + d.Pos.Length;
                }
                if (near.Count == 0) return "none of her body meshes could be read (upperBody / lowerBody / foot / neck)";

                // 4. first pass: every avatar vertex in her bind pose (rest -> aligned rest by its own weights)
                var pass = new List<Posed>();
                foreach (var r in avRends)
                {
                    var src3 = r.sharedMesh;
                    var v = src3.vertices; var nr = src3.normals; var tn = src3.tangents; var bw = src3.boneWeights;
                    var bIdx = r.bones.Select(b => b != null && idx.TryGetValue(b, out var bi) ? bi : -1).ToArray();
                    var l2w = r.transform.localToWorldMatrix;
                    int nv = v.Length;
                    var ps = new Posed { R = r, P = new Vector3[nv], N = nr != null && nr.Length == nv ? new Vector3[nv] : null, T = tn != null && tn.Length == nv ? new Vector4[nv] : null,
                                         Dom = new int[nv], Mask = new int[nv], MapShare = new float[nv], W = bw, BIdx = bIdx };
                    for (int vi = 0; vi < nv; vi++)
                    {
                        var p = HerRoot.InverseTransformPoint(l2w.MultiplyPoint3x4(v[vi]));
                        var w = bw.Length == nv ? bw[vi] : new BoneWeight { boneIndex0 = 0, weight0 = 1f };
                        Vector3 pp = Vector3.zero, np = Vector3.zero, tp = Vector3.zero;
                        Vector3 nIn = ps.N != null ? HerRoot.InverseTransformDirection(r.transform.TransformDirection(nr[vi])) : Vector3.zero;
                        Vector3 tIn = ps.T != null ? HerRoot.InverseTransformDirection(r.transform.TransformDirection(tn[vi])) : Vector3.zero;
                        float ws = 0f, domW = -1f, mapW = 0f; int dom = -1, mask = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            int bi = k == 0 ? w.boneIndex0 : k == 1 ? w.boneIndex1 : k == 2 ? w.boneIndex2 : w.boneIndex3;
                            float wt = k == 0 ? w.weight0 : k == 1 ? w.weight1 : k == 2 ? w.weight2 : w.weight3;
                            if (wt <= 0f || bi < 0 || bi >= bIdx.Length || bIdx[bi] < 0) continue;
                            int a = bIdx[bi];
                            pp += wt * (Rb[a] * p + tb[a]); np += wt * (Rb[a] * nIn); tp += wt * (Rb[a] * tIn); ws += wt;
                            if (wt > domW) { domW = wt; dom = a; }
                            if (ByMap(avRegion[a])) mapW += wt;
                            else if (wt >= 0.1f) mask |= 1 << avRegion[a];
                        }
                        ps.P[vi] = ws > 1e-6f ? pp / ws : p;
                        if (ps.N != null) ps.N[vi] = np.sqrMagnitude > 1e-12f ? np.normalized : nIn;
                        if (ps.T != null) { var t3 = tp.sqrMagnitude > 1e-12f ? tp.normalized : tIn; ps.T[vi] = new Vector4(t3.x, t3.y, t3.z, tn[vi].w); }
                        ps.Dom[vi] = dom; ps.MapShare[vi] = ws > 1e-6f ? mapW / ws : 1f;
                        if (mask == 0 && dom >= 0 && !ByMap(avRegion[dom])) mask = 1 << avRegion[dom];
                        if (mask == 0 && ps.MapShare[vi] < 0.999f) mask = 1 << 1;   // a little weight on small bones: the torso
                        ps.Mask[vi] = mask;
                    }
                    // which vertices take her face weights: skin (not the mouth cavity), lashes, brows; eyes, teeth, mouth, tear by the bone map
                    ps.FaceOk = new bool[nv];
                    var pm = r.sharedMaterials;
                    for (int sm = 0; sm < src3.subMeshCount; sm++)
                    {
                        string sn2 = sm < pm.Length && pm[sm] != null ? pm[sm].name : "";
                        string rl2 = _surfRole.TryGetValue(sn2, out var ro2) ? ro2 : "skin";
                        bool ok = (rl2 == "skin" && sn2.ToLowerInvariant().IndexOf("mouth", StringComparison.Ordinal) < 0) || rl2 == "lashes" || rl2 == "brows";
                        if (!ok) continue;
                        foreach (var ix in src3.GetIndices(sm)) ps.FaceOk[ix] = true;
                    }
                    pass.Add(ps);
                }

                // 5. fit: her joints as landmarks (no spine / collar joints: rig conventions), then the soles (heel and ball) at her
                //    soles' height so the feet stand on her floor
                var src = new List<Vector3>(); var dst = new List<Vector3>();
                foreach (var key in new[] { "head", "eye", "upperarm", "forearm", "hand", "thumb1", "thumb2", "thumb3", "index1", "index2", "index3", "middle1", "middle2", "middle3",
                                            "ring1", "ring2", "ring3", "pinky1", "pinky2", "pinky3", "thigh", "shin", "foot", "toe" })
                    foreach (var side in key == "head" ? new string[] { null } : new[] { "L", "R" })
                    {
                        int i = I(K(key, side));
                        if (i < 0 || herOf[i] == null) continue;
                        src.Add(X[i]); dst.Add(HP(herOf[i]));
                    }
                int joints = src.Count;
                var fitFace = new List<Vector3>(); var herFaceLm = new List<Vector3>(); string faceNote = "";
                var rbf = Rbf.Fit(src, dst);
                var mainPass = pass.OrderByDescending(x => x.P.Length).First();
                string soles = "";
                foreach (var side in new[] { "L", "R" })
                {
                    int fa = I(K("foot", side)), ft = I(K("toe", side));
                    if (fa < 0 || ft < 0 || herOf[fa] == null || herOf[ft] == null) continue;
                    int reg = Region(K("foot", side));
                    for (int part = 0; part < 2; part++)   // heel, ball
                    {
                        var a0 = X[fa]; var dir = X[ft] - a0; float len = dir.magnitude; dir /= Mathf.Max(len, 1e-5f);
                        int lo = -1;
                        for (int vi = 0; vi < mainPass.P.Length; vi++)
                        {
                            int dm = mainPass.Dom[vi];
                            if (dm < 0 || avRegion[dm] != reg) continue;
                            float t = Vector3.Dot(mainPass.P[vi] - a0, dir) / Mathf.Max(len, 1e-5f);
                            if (part == 0 ? t > 0.3f : t < 0.7f) continue;
                            if (lo < 0 || mainPass.P[vi].y < mainPass.P[lo].y) lo = vi;
                        }
                        float herY = near.Lowest(reg, HP(herOf[fa]), HP(herOf[ft]), part == 0);
                        if (lo < 0 || float.IsNaN(herY)) continue;
                        var s0 = mainPass.P[lo]; var d0 = rbf.Apply(s0);
                        soles += (soles.Length > 0 ? ", " : "") + side + (part == 0 ? " heel " : " ball ") + ((d0.y - herY) * 1000f).ToString("0", CultureInfo.InvariantCulture) + " mm";
                        d0.y = herY;
                        src.Add(s0); dst.Add(d0);
                    }
                }
                if (src.Count > joints) rbf = Rbf.Fit(src, dst);

                // the face (0.11.43): her face rig on the avatar's face. Her face skin's weights (lips, cheeks, brows, lids, jaw) are
                // looked up through a warp of the avatar's face landmarks onto hers (lip corners and middles, brows, eyes, head); the
                // avatar's face keeps its own shape (the warp only finds where on her face each vertex is), so the game's expressions,
                // face clips and lip sync move its lips, brows and lids as they move hers
                var faceSkin = new SkinGrid(0.01f);
                Rbf faceWarp = null;
                {
                    AvatarFace.TryGetValue(best.Id, out var af);
                    var fs = new List<Vector3>(); var fd = new List<Vector3>(); var got = new List<string>();
                    foreach (var key in new[] { "head", "eye.L", "eye.R" })
                    {
                        int i = I(key);
                        if (i >= 0 && herOf[i] != null) { fs.Add(rbf.Apply(X[i])); fd.Add(HP(herOf[i])); }
                    }
                    if (af != null)
                        foreach (var k in FaceKeys)
                        {
                            var at = af.TryGetValue(k, out var an) ? FindAny(avByName, an) : null;
                            var ht = HerFace.TryGetValue(k, out var hn) ? FindAny(herByName, hn) : null;
                            if (at == null || ht == null || !idx.ContainsKey(at) || !herBind.ContainsKey(ht)) continue;
                            fs.Add(rbf.Apply(X[idx[at]])); fd.Add(HP(ht)); got.Add(k);
                        }
                    if (got.Count >= 4)
                    {
                        faceWarp = Rbf.Fit(fs, fd);
                        foreach (var r in HerRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        {
                            if (r == null || r.sharedMesh == null || VrHands.UnderSf(r.transform) || !r.gameObject.activeInHierarchy) continue;
                            var nm = r.name.ToLowerInvariant();
                            if (!nm.Contains("face")) continue;
                            var d = MeshData.Read(r.sharedMesh, out var err);
                            if (d == null || d.Pos == null || d.Weights == null) { Plugin.Log.LogWarning("Avatar: couldn't read her face " + r.name + ": " + err); continue; }
                            var bones = r.bones; var real = new Transform[bones.Length];
                            for (int b = 0; b < bones.Length; b++)
                            {
                                var t = bones[b];
                                while (t != null && (t.name.StartsWith("Sf", StringComparison.Ordinal) || !herBind.ContainsKey(t))) t = t.parent;
                                real[b] = t;
                            }
                            faceSkin.Add(d, real, herW2L * r.transform.localToWorldMatrix, t => 0);
                            faceNote = r.name;
                        }
                        float fe = 0f; for (int i = 0; i < fs.Count; i++) fe = Mathf.Max(fe, (fd[i] - fs[i]).magnitude);
                        faceNote = faceSkin.Count > 0 ? "face: her rig from " + faceNote + " by " + got.Count + " landmarks (" + string.Join(" ", got.ToArray()) + "), moved up to " +
                                                        (fe * 1000f).ToString("0", CultureInfo.InvariantCulture) + " mm for the lookup"
                                                      : "face: her face mesh not readable (jaw / eyes / head only)";
                    }
                    else faceNote = "face: the " + best.Id + " rig has " + got.Count + " of the face landmarks (jaw / eyes / head only)";
                }
                float fitErr = 0f; for (int i = 0; i < src.Count; i++) fitErr = Mathf.Max(fitErr, (rbf.Apply(src[i]) - dst[i]).magnitude);
                float moveMax = 0f; for (int i = 0; i < joints; i++) moveMax = Mathf.Max(moveMax, (dst[i] - src[i]).magnitude);

                // 6. the new meshes, bound to her bones
                Go = new GameObject("SfAvatar " + Name);
                Go.transform.SetPositionAndRotation(HerRoot.position, HerRoot.rotation);
                var herBones = new List<Transform>(); var herIndex = new Dictionary<Transform, int>();
                int Bi(Transform t) { if (!herIndex.TryGetValue(t, out var i)) { i = herBones.Count; herBones.Add(t); herIndex[t] = i; } return i; }
                bool lit = LitTemplate() != null;
                var herBody = HerBodyRenderer();
                _herSkin = herBody != null ? herBody.sharedMaterials.FirstOrDefault(m => m != null && m.shader != null && m.shader.name.Contains("Skin")) : null;
                int fromHer = 0, fromMap = 0, far = 0, verts = 0, fromFace = 0;
                var built = new List<KeyValuePair<SkinnedMeshRenderer, Mesh>>();
                var acc = new Dictionary<Transform, float>(); var hacc = new Dictionary<Transform, float>();
                int snapped = 0, cutTris = 0;
                var ringUv = new List<Vector2>(); Texture ringTex = null;
                foreach (var ps in pass)
                {
                    var r = ps.R; var src3 = r.sharedMesh; int nv = ps.P.Length;
                    var nv3 = new Vector3[nv]; var nbw = new BoneWeight[nv];
                    var inside = new bool[nv]; var snapW = new float[nv];
                    for (int vi = 0; vi < nv; vi++)
                    {
                        var q = rbf.Apply(ps.P[vi]);
                        // the crotch: a vertex nearer her genital patch than her skin is cut out; the rest within 3 cm of the patch or her
                        // pubic hair is laid fully onto her body surface (the tangent plane of her nearest skin vertex), fading out to 8 cm
                        if (patch.Count > 0)
                        {
                            float dP = patch.Dist(q, 0.1f), dH = pubic.Count > 0 ? pubic.Dist(q, 0.1f) : 1f, dR = Mathf.Min(dP, dH);
                            // inside: nearer her patch than her skin, as fitted (laid onto her skin first, the skin over her vulva would
                            // land on her inner thighs' edge and cover it)
                            if (dP < 0.04f && dP < herSurf.Dist(q, 0.1f)) inside[vi] = true;
                            else if (dR < 0.08f && herSurf.Nearest(q, 0.08f, out int si, out float sd))
                            {
                                float sw = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.03f, 0.08f, dR));
                                var sp = herSurf.P[si]; var sn = herSurf.N[si];
                                q = Vector3.Lerp(q, q - Vector3.Dot(q - sp, sn) * sn, sw);
                                if (ps.N != null) ps.N[vi] = Vector3.Lerp(ps.N[vi], sn, sw).normalized;
                                snapW[vi] = sw;
                                if (sw > 0f) snapped++;
                            }
                        }
                        nv3[vi] = q;
                        // weights: the share on head / hand / foot bones by the bone map, the rest her own (the nearest skin of the
                        // regions it covers); no such skin within 4 cm: all by the bone map. Shares keep the seams at the neck, wrists
                        // and ankles smooth.
                        acc.Clear();
                        if (faceWarp != null && faceSkin.Count > 0 && ps.FaceOk != null && ps.FaceOk[vi] && ps.Dom[vi] >= 0 && avRegion[ps.Dom[vi]] == 0)
                        {
                            if (faceSkin.Nearest(faceWarp.Apply(q), 0.02f, 1, acc)) { nbw[vi] = Top4(acc, Bi); fromFace++; continue; }
                            acc.Clear();
                        }
                        float ms = ps.MapShare[vi];
                        var w = ps.W.Length == nv ? ps.W[vi] : new BoneWeight { boneIndex0 = 0, weight0 = 1f };
                        bool her = false;
                        if (ms < 0.999f)
                        {
                            hacc.Clear();
                            her = near.Nearest(q, 0.04f, ps.Mask[vi], hacc);
                            if (her) { foreach (var kv in hacc) acc[kv.Key] = (1f - ms) * kv.Value; fromHer++; }
                            else far++;
                        }
                        if (!her) fromMap++;
                        for (int k = 0; k < 4; k++)
                        {
                            int bi = k == 0 ? w.boneIndex0 : k == 1 ? w.boneIndex1 : k == 2 ? w.boneIndex2 : w.boneIndex3;
                            float wt = k == 0 ? w.weight0 : k == 1 ? w.weight1 : k == 2 ? w.weight2 : w.weight3;
                            if (wt <= 0f || bi < 0 || bi >= ps.BIdx.Length || ps.BIdx[bi] < 0) continue;
                            int a = ps.BIdx[bi];
                            if (her && !ByMap(avRegion[a])) continue;   // that share came from her skin
                            foreach (var kv in tgt[a]) { acc.TryGetValue(kv.Key, out var o); acc[kv.Key] = o + wt * kv.Value; }
                        }
                        if (acc.Count == 0) acc[herRootBone] = 1f;
                        nbw[vi] = Top4(acc, Bi);
                    }
                    verts += nv;
                    var mesh = new Mesh { name = src3.name + " (on her)", indexFormat = nv > 65000 ? IndexFormat.UInt32 : src3.indexFormat };
                    mesh.vertices = nv3;
                    if (ps.N != null) mesh.normals = ps.N;
                    if (ps.T != null) mesh.tangents = ps.T;
                    for (int c = 0; c < 4; c++)
                        if (src3.HasVertexAttribute(VertexAttribute.TexCoord0 + c)) { var uv = new List<Vector4>(); src3.GetUVs(c, uv); mesh.SetUVs(c, uv); }
                    if (src3.HasVertexAttribute(VertexAttribute.Color)) mesh.colors = src3.colors;
                    mesh.boneWeights = nbw;
                    var mats = r.sharedMaterials;
                    mesh.subMeshCount = src3.subMeshCount;
                    for (int s = 0; s < src3.subMeshCount; s++)
                    {
                        string surf = s < mats.Length && mats[s] != null ? mats[s].name : "";
                        string role = _surfRole.TryGetValue(surf, out var ro) ? ro : "skin";
                        bool leftOut = role == "cornea" || role == "tear" || ((role == "lashes" || role == "brows") && !lit);   // see-through overlays: not in this version
                        var ind = leftOut ? new int[0] : src3.GetIndices(s);
                        if (!leftOut && src3.GetTopology(s) == MeshTopology.Triangles && inside.Any(x => x))
                        {
                            var keep = new List<int>(ind.Length);
                            for (int t = 0; t + 2 < ind.Length; t += 3)
                            {
                                if (inside[ind[t]] || inside[ind[t + 1]] || inside[ind[t + 2]]) { cutTris++; continue; }
                                keep.Add(ind[t]); keep.Add(ind[t + 1]); keep.Add(ind[t + 2]);
                            }
                            ind = keep.ToArray();
                        }
                        mesh.SetIndices(ind, src3.GetTopology(s), s, false);
                        // the skin just around the patch, for the patch's tint: its colour map at those vertices
                        if (!leftOut && role == "skin" && ringTex == null)
                        {
                            var uv0 = src3.uv;
                            var tex = _surfTex.TryGetValue(surf, out var tx) ? Tex(tx, "albedo") : null;
                            if (tex != null && uv0 != null && uv0.Length == nv)
                            {
                                var ring = new List<Vector2>();
                                foreach (var vi in ind.Distinct()) if (snapW[vi] > 0.5f && !inside[vi]) ring.Add(uv0[vi]);
                                if (ring.Count > 20) { ringTex = tex; ringUv = ring; }
                            }
                        }
                    }
                    _meshes.Add(mesh);
                    built.Add(new KeyValuePair<SkinnedMeshRenderer, Mesh>(r, mesh));
                }
                _herBones = herBones.ToArray();
                var herBindposes = _herBones.Select(t => herBind[t].inverse).ToArray();   // renderer at her root: bone^-1 in her root's space
                foreach (var kv in built)
                {
                    kv.Value.bindposes = herBindposes;
                    kv.Value.RecalculateBounds();
                    var go = new GameObject(kv.Key.name);
                    go.transform.SetParent(Go.transform, false);
                    var sr = go.AddComponent<SkinnedMeshRenderer>();
                    sr.sharedMesh = kv.Value;
                    sr.bones = _herBones;
                    sr.rootBone = herRootBone;
                    sr.updateWhenOffscreen = true;
                    sr.skinnedMotionVectors = true;
                    if (herBody != null)
                    {
                        go.layer = herBody.gameObject.layer;
                        sr.renderingLayerMask = herBody.renderingLayerMask;
                        sr.shadowCastingMode = herBody.shadowCastingMode;
                        sr.receiveShadows = herBody.receiveShadows;
                        sr.lightProbeUsage = herBody.lightProbeUsage;
                        sr.reflectionProbeUsage = herBody.reflectionProbeUsage;
                    }
                    _rends.Add(sr);
                    _origMats.Add(kv.Key.sharedMaterials);
                }
                // the patch's tint: its _SkinColor scaled so her skin around it (her colour map x _SkinColor) comes out at the avatar's
                // skin around it; set per renderer (a property block), so her shared body material and the game's skin colour stay
                string tintNote = "no tint (no colour maps)";
                if (ringTex != null && _herSkin != null && _herSkin.HasProperty("_ColorMap") && _herSkin.GetTexture("_ColorMap") != null)
                {
                    var herUv = herSurf.UvNear(patch, 0.03f);
                    var a = MeanAt(ringTex, ringUv);
                    var h = MeanAt(_herSkin.GetTexture("_ColorMap"), herUv);
                    var ht = _herSkin.HasProperty("_SkinColor") ? _herSkin.GetColor("_SkinColor") : Color.white;
                    if (h.r > 1e-3f && h.g > 1e-3f && h.b > 1e-3f && herUv.Count > 20)
                    {
                        _patchRatio = new Color(Mathf.Clamp(a.r / (h.r * ht.r), 0.3f, 3f), Mathf.Clamp(a.g / (h.g * ht.g), 0.3f, 3f), Mathf.Clamp(a.b / (h.b * ht.b), 0.3f, 3f), 1f);
                        tintNote = "tint " + _patchRatio.r.ToString("0.00", CultureInfo.InvariantCulture) + " " + _patchRatio.g.ToString("0.00", CultureInfo.InvariantCulture) + " " +
                                   _patchRatio.b.ToString("0.00", CultureInfo.InvariantCulture) + " (avatar skin around it " + ringUv.Count + " vertices, hers " + herUv.Count + ")";
                    }
                }
                Materials(MaterialRoute.Value, Brightness.Value);
                Hide(KeepHair.Value);
                string crotch = patch.Count > 0 ? "crotch: " + snapped + " vertices laid onto her skin, " + cutTris + " triangles cut where her genital meshes sit, " + tintNote
                                                : "crotch: no genital mesh of hers found (nothing cut)";

                Summary = Name + ": " + best.Id + " rig, " + mapped + " bones mapped, fitted to her " + joints + " joints (moved up to " +
                          (moveMax * 100f).ToString("0.0", CultureInfo.InvariantCulture) + " cm), " + _rends.Count + " meshes on " + _herBones.Length + " of her bones";
                Plugin.Log.LogInfo("Avatar " + Summary + "; " + verts + " vertices: " + fromHer + " with her skin weights (head, hand and foot shares by the bone map), " + fromMap + " by the bone map alone (" + far +
                                   " of them farther than 4 cm from her skin); x" + scale.ToString("0.000", CultureInfo.InvariantCulture) + ", facing turned " +
                                   yaw.ToString("0.#", CultureInfo.InvariantCulture) + " deg, fit residual " + (fitErr * 1000f).ToString("0.0", CultureInfo.InvariantCulture) +
                                   " mm, soles before the fit " + (soles.Length > 0 ? soles : "-") + "; " + faceNote + " (" + fromFace + " vertices); " + crotch + "; her meshes " + herMeshes + "; her skin material " + (_herSkin != null ? _herSkin.name : "not found") + "; " + _hidden.Count +
                                   " of her renderers hidden");
                return null;
            }

            class Posed { public SkinnedMeshRenderer R; public Vector3[] P, N; public Vector4[] T; public int[] Dom, Mask, BIdx; public float[] MapShare; public BoneWeight[] W; public bool[] FaceOk; }

            // 0 head, 1 torso, 2/3 upper arm, 4/5 forearm, 6/7 hand, 8/9 thigh, 10/11 shin, 12/13 foot (L/R)
            static int Region(string key)
            {
                int side = key.EndsWith(".R", StringComparison.Ordinal) ? 1 : 0;
                string k = key.Split('.')[0];
                if (k == "head" || k == "jaw" || k == "eye") return 0;
                if (k == "upperarm") return 2 + side;
                if (k == "forearm") return 4 + side;
                if (k == "hand" || k.StartsWith("thumb", StringComparison.Ordinal) || k.StartsWith("index", StringComparison.Ordinal) || k.StartsWith("middle", StringComparison.Ordinal)
                    || k.StartsWith("ring", StringComparison.Ordinal) || k.StartsWith("pinky", StringComparison.Ordinal)) return 6 + side;
                if (k == "thigh") return 8 + side;
                if (k == "shin") return 10 + side;
                if (k == "foot" || k == "toe") return 12 + side;
                return 1;
            }

            static BoneWeight Top4(Dictionary<Transform, float> acc, Func<Transform, int> index)
            {
                var top = acc.Where(kv => kv.Key != null && kv.Value > 0f).OrderByDescending(kv => kv.Value).Take(4).ToList();
                float s = top.Sum(kv => kv.Value);
                if (s <= 0f) return new BoneWeight();
                var w = new BoneWeight();
                for (int k = 0; k < top.Count; k++)
                {
                    int i = index(top[k].Key); float x = top[k].Value / s;
                    if (k == 0) { w.boneIndex0 = i; w.weight0 = x; }
                    else if (k == 1) { w.boneIndex1 = i; w.weight1 = x; }
                    else if (k == 2) { w.boneIndex2 = i; w.weight2 = x; }
                    else { w.boneIndex3 = i; w.weight3 = x; }
                }
                return w;
            }

            static float P(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }

            // her bind pose in her root's space from her renderers' bind poses (a bone in several meshes: the most common; a side
            // bone placed differently by copies, Eye_R: the one mirroring its counterpart, as rig.py does)
            Dictionary<Transform, Matrix4x4> HerBind()
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
                        if (bones[i] == null || bones[i].name.StartsWith("Sf", StringComparison.Ordinal)) continue;
                        var m = mr * bp[i].inverse;
                        if (!cand.TryGetValue(bones[i], out var l)) cand[bones[i]] = l = new List<KeyValuePair<Matrix4x4, int>>();
                        int at = l.FindIndex(x => Close(x.Key, m));
                        if (at >= 0) l[at] = new KeyValuePair<Matrix4x4, int>(l[at].Key, l[at].Value + 1); else l.Add(new KeyValuePair<Matrix4x4, int>(m, 1));
                    }
                }
                var outp = new Dictionary<Transform, Matrix4x4>();
                foreach (var kv in cand) outp[kv.Key] = kv.Value.OrderByDescending(x => x.Value).First().Key;
                foreach (var kv in cand)
                {
                    if (kv.Value.Count < 2) continue;
                    string nm = kv.Key.name;
                    if (!(nm.EndsWith("_L", StringComparison.Ordinal) || nm.EndsWith("_R", StringComparison.Ordinal))) continue;
                    string o = nm.Substring(0, nm.Length - 2) + (nm.EndsWith("_L", StringComparison.Ordinal) ? "_R" : "_L");
                    var ot = cand.Keys.FirstOrDefault(t => t.name == o);
                    if (ot == null || cand[ot].Count != 1) continue;
                    Vector3 mp = cand[ot][0].Key.GetColumn(3); mp.x = -mp.x;
                    outp[kv.Key] = kv.Value.OrderBy(x => ((Vector3)x.Key.GetColumn(3) - mp).sqrMagnitude).First().Key;
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

            // ---- what the avatar replaces: her skin, face parts and (optionally) hair; given back exactly ----
            // Kept: her genital patch (tinted), pubic hair, the internal meshes, clothes, toys and props.
            static bool IsPatch(string lowerName) { return lowerName.Contains("pussy"); }

            static bool Replaced(Renderer r, string n)
            {
                if (IsPatch(n) || n.Contains("pubichair") || n.Contains("semen") || n.Contains("uterus") || n.Contains("rectum")) return false;
                if (n.StartsWith("reika_face", StringComparison.Ordinal)) return true;
                foreach (var k in new[] { "sclera", "iris", "eye_glow", "eyeglow", "eyelash", "eyebrow", "tears", "uppermouth", "lowermouth", "teeth", "tongue" })
                    if (n.Contains(k) && n.StartsWith("reika", StringComparison.Ordinal)) return true;
                foreach (var m in r.sharedMaterials) if (m != null && m.shader != null && m.shader.name.Contains("Skin")) return true;   // her body skin, nipples
                return false;
            }

            public void Hide(bool keepHair)
            {
                Hair = keepHair;
                if (HerRoot == null) return;
                foreach (var r in HerRoot.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || VrHands.UnderSf(r.transform)) continue;
                    var n = r.name.ToLowerInvariant();
                    bool hair = n.Contains("hair") && !n.Contains("pubic");
                    bool hide = hair ? !keepHair : Replaced(r, n);
                    if (IsPatch(n)) Tint(r);
                    if (!hide)
                    {
                        if (_hidden.TryGetValue(r, out var was)) { r.forceRenderingOff = was; _hidden.Remove(r); }
                        continue;
                    }
                    if (_hidden.ContainsKey(r)) { r.forceRenderingOff = true; continue; }
                    _hidden[r] = r.forceRenderingOff;
                    r.forceRenderingOff = true;
                }
            }

            // her genital patch in the avatar's skin tone: _SkinColor of its own material x the measured ratio x brightness
            Color _patchRatio = Color.white;
            readonly HashSet<Renderer> _tinted = new HashSet<Renderer>();
            MaterialPropertyBlock _mpb;
            static readonly int _idSkinColor = Shader.PropertyToID("_SkinColor");

            void Tint(Renderer r)
            {
                var m = r.sharedMaterial;
                if (m == null || !m.HasProperty(_idSkinColor)) return;
                if (_mpb == null) _mpb = new MaterialPropertyBlock();
                var c = m.GetColor(_idSkinColor);
                float b = Bright > 0f ? Bright : 1f;
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor(_idSkinColor, new Color(c.r * _patchRatio.r * b, c.g * _patchRatio.g * b, c.b * _patchRatio.b * b, c.a));
                r.SetPropertyBlock(_mpb);
                _tinted.Add(r);
            }

            // the mean colour of a texture at some UVs (linear): a small copy on the GPU, read back once at load
            static Color MeanAt(Texture tex, List<Vector2> uvs)
            {
                if (tex == null || uvs == null || uvs.Count == 0) return Color.black;
                var rt = RenderTexture.GetTemporary(256, 256, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                Graphics.Blit(tex, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var t = new Texture2D(256, 256, TextureFormat.RGBAHalf, false, true);
                t.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
                t.Apply(false);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                Color s = Color.black;
                foreach (var uv in uvs) s += t.GetPixelBilinear(uv.x - Mathf.Floor(uv.x), uv.y - Mathf.Floor(uv.y));
                Kill(t);
                return s / uvs.Count;
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

            // a game material on HDRP/Lit (opaque, standard if there is one); never one of ours
            static Material LitTemplate()
            {
                Material best = null;
                foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (m == null || m.shader == null || m.shader.name != "HDRP/Lit" || m.name.StartsWith("SfAvatar", StringComparison.Ordinal)) continue;
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
                foreach (var m in _made) Kill(m);
                _made.Clear(); _tint.Clear();
                Route = route; Bright = bright;
                var lit = LitTemplate();
                var report = new List<string>();
                for (int ri = 0; ri < _rends.Count; ri++)
                {
                    var mats = (Material[])_origMats[ri].Clone();
                    for (int s = 0; s < mats.Length; s++)
                    {
                        string surf = mats[s] != null ? mats[s].name : "";
                        string role = _surfRole.TryGetValue(surf, out var ro) ? ro : "skin";
                        _surfTex.TryGetValue(surf, out var tex);
                        var col = _surfCol.TryGetValue(surf, out var cc) ? cc : Color.white;
                        Material m = null;
                        if (role == "cornea" || role == "tear" || ((role == "lashes" || role == "brows") && lit == null)) { report.Add(surf + " left out"); continue; }
                        if ((role == "skin" || role == "nails") && route == 0 && _herSkin != null) m = SkinCopy(tex);
                        else if (lit != null) m = LitCopy(lit, role, tex, col);
                        else if (_herSkin != null) m = SkinCopy(tex);
                        if (m != null) { m.name = "SfAvatar " + surf; _made.Add(m); mats[s] = m; }
                    }
                    _rends[ri].sharedMaterials = mats;
                }
                SetBrightness(bright);
                Plugin.Log.LogInfo("Avatar materials: " + (route == 0 ? "her skin shader" : "HDRP/Lit") + " for the skin, Lit " + (lit != null ? "from " + lit.name : "missing") +
                                   (report.Count > 0 ? "; " + string.Join(", ", report.ToArray()) : ""));
            }

            public void SetBrightness(float b)
            {
                Bright = b;
                foreach (var r in _tinted) if (r != null) Tint(r);
                foreach (var kv in _tint)
                {
                    if (kv.Key == null) continue;
                    var c = new Color(kv.Value.r * b, kv.Value.g * b, kv.Value.b * b, kv.Value.a);
                    if (kv.Key.HasProperty("_BaseColor")) kv.Key.SetColor("_BaseColor", c);
                    else if (kv.Key.HasProperty("_SkinColor")) kv.Key.SetColor("_SkinColor", c);
                }
            }

            Material SkinCopy(Dictionary<string, string> tex)
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
                F("_CutoutSize", 0f);
                _tint.Add(new KeyValuePair<Material, Color>(m, Color.white));
                return m;
            }

            Material LitCopy(Material tpl, string role, Dictionary<string, string> tex, Color col)
            {
                var m = new Material(tpl);
                bool cut = role == "lashes" || role == "brows";
                bool sss = (role == "skin" || role == "mouth") && _herSkin != null && _herSkin.HasProperty("_DiffusionProfileHash");
                var albedo = cut ? Tex(tex, "albedoalpha") ?? Tex(tex, "albedo") : Tex(tex, "albedo");
                var normal = Tex(tex, "normal");
                var mask = Tex(tex, "mask");
                m.SetTexture("_BaseColorMap", albedo);
                var baseCol = albedo != null ? Color.white : new Color(col.r, col.g, col.b, 1f);
                m.SetColor("_BaseColor", baseCol);
                if (role == "skin" || role == "nails" || role == "mouth") _tint.Add(new KeyValuePair<Material, Color>(m, baseCol));
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

            static void Kill(UnityEngine.Object o)
            {
                if (o == null) return;
                if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
            }

            public void Destroy()
            {
                foreach (var kv in _hidden) if (kv.Key != null) kv.Key.forceRenderingOff = kv.Value;
                _hidden.Clear();
                foreach (var r in _tinted) if (r != null) r.SetPropertyBlock(null);
                _tinted.Clear();
                Kill(Go);
                foreach (var m in _made) Kill(m);
                _made.Clear(); _tint.Clear();
                foreach (var m in _meshes) Kill(m);
                _meshes.Clear();
                if (_bundle != null) { _bundle.Unload(true); _bundle = null; }
            }
        }

        // points of her meshes in her root's space (positions, normals, UVs) on a grid: distances and nearest points
        class PointGrid
        {
            readonly float _cell;
            readonly Dictionary<long, List<int>> _grid = new Dictionary<long, List<int>>();
            public readonly List<Vector3> P = new List<Vector3>(), N = new List<Vector3>();
            public readonly List<Vector2> Uv = new List<Vector2>();
            public int Count { get { return P.Count; } }

            public PointGrid(float cell) { _cell = cell; }

            long Key(int x, int y, int z) { return ((long)(x + 100000) << 40) ^ ((long)(y + 100000) << 20) ^ (long)(z + 100000); }

            public void Add(MeshData d, Matrix4x4 toRoot)
            {
                var nrm = d.Nrm != null && d.Nrm.Length == d.Pos.Length ? d.Nrm : Normals(d);
                for (int i = 0; i < d.Pos.Length; i++)
                {
                    var p = toRoot.MultiplyPoint3x4(d.Pos[i]);
                    long key = Key(Mathf.FloorToInt(p.x / _cell), Mathf.FloorToInt(p.y / _cell), Mathf.FloorToInt(p.z / _cell));
                    if (!_grid.TryGetValue(key, out var l)) _grid[key] = l = new List<int>();
                    l.Add(P.Count);
                    P.Add(p);
                    var n = toRoot.MultiplyVector(nrm[i]);
                    N.Add(n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up);
                    Uv.Add(d.Uv != null && i < d.Uv.Length ? d.Uv[i] : Vector2.zero);
                }
            }

            static Vector3[] Normals(MeshData d)
            {
                var n = new Vector3[d.Pos.Length];
                foreach (var t in d.Sub)
                    for (int i = 0; i + 2 < t.Length; i += 3)
                    {
                        var f = Vector3.Cross(d.Pos[t[i + 1]] - d.Pos[t[i]], d.Pos[t[i + 2]] - d.Pos[t[i]]);
                        n[t[i]] += f; n[t[i + 1]] += f; n[t[i + 2]] += f;
                    }
                return n;
            }

            public bool Nearest(Vector3 q, float max, out int best, out float dist)
            {
                int cx = Mathf.FloorToInt(q.x / _cell), cy = Mathf.FloorToInt(q.y / _cell), cz = Mathf.FloorToInt(q.z / _cell);
                int r = Mathf.CeilToInt(max / _cell);
                best = -1; float bd = max * max;
                for (int x = cx - r; x <= cx + r; x++)
                    for (int y = cy - r; y <= cy + r; y++)
                        for (int z = cz - r; z <= cz + r; z++)
                        {
                            if (!_grid.TryGetValue(Key(x, y, z), out var l)) continue;
                            foreach (var i in l) { float dd = (P[i] - q).sqrMagnitude; if (dd < bd) { bd = dd; best = i; } }
                        }
                dist = best >= 0 ? Mathf.Sqrt(bd) : max;
                return best >= 0;
            }

            public float Dist(Vector3 q, float max) { Nearest(q, max, out _, out float d); return d; }

            // UVs of the points within max of another set (her skin around her genital patch)
            public List<Vector2> UvNear(PointGrid other, float max)
            {
                var l = new List<Vector2>();
                for (int i = 0; i < P.Count; i++) if (other.Dist(P[i], max) < max) l.Add(Uv[i]);
                return l;
            }
        }

        // her body skin for nearest-vertex weights: positions in her root's space with their bones and weights, on a grid
        class SkinGrid
        {
            readonly float _cell;
            readonly Dictionary<long, List<int>> _grid = new Dictionary<long, List<int>>();
            readonly List<Vector3> _p = new List<Vector3>();
            readonly List<Transform[]> _b = new List<Transform[]>();
            readonly List<Vector4> _w = new List<Vector4>();
            readonly List<int> _r = new List<int>();
            public int Count { get { return _p.Count; } }

            public SkinGrid(float cell) { _cell = cell; }

            long Key(int x, int y, int z) { return ((long)(x + 100000) << 40) ^ ((long)(y + 100000) << 20) ^ (long)(z + 100000); }

            public void Add(MeshData d, Transform[] bones, Matrix4x4 toRoot, Func<Transform, int> region)
            {
                for (int i = 0; i < d.Pos.Length && i < d.Weights.Length; i++)
                {
                    var w = d.Weights[i];
                    var b = new Transform[4];
                    int[] bi = { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 };
                    for (int k = 0; k < 4; k++) b[k] = bi[k] >= 0 && bi[k] < bones.Length ? bones[bi[k]] : null;
                    var p = toRoot.MultiplyPoint3x4(d.Pos[i]);
                    int id = _p.Count;
                    var wv = new Vector4(w.weight0, w.weight1, w.weight2, w.weight3);
                    int mk = 0; for (int k = 1; k < 4; k++) if (wv[k] > wv[mk]) mk = k;
                    _p.Add(p); _b.Add(b); _w.Add(wv); _r.Add(region(b[mk]));
                    long key = Key(Mathf.FloorToInt(p.x / _cell), Mathf.FloorToInt(p.y / _cell), Mathf.FloorToInt(p.z / _cell));
                    if (!_grid.TryGetValue(key, out var l)) _grid[key] = l = new List<int>();
                    l.Add(id);
                }
            }

            // the lowest vertex of a region in the rear (heel) or front (ball) part between ankle and toe joints; NaN if none
            public float Lowest(int region, Vector3 ankle, Vector3 toe, bool heel)
            {
                var dir = toe - ankle; float len = dir.magnitude; if (len < 1e-5f) return float.NaN; dir /= len;
                float best = float.NaN;
                for (int i = 0; i < _p.Count; i++)
                {
                    if (_r[i] != region) continue;
                    float t = Vector3.Dot(_p[i] - ankle, dir) / len;
                    if (heel ? t > 0.3f : t < 0.7f) continue;
                    if (float.IsNaN(best) || _p[i].y < best) best = _p[i].y;
                }
                return best;
            }

            // her skin near q (regions in the mask, within max): the weights of the 4 nearest vertices blended by inverse distance into
            // acc (one nearest vertex gave the left and right inner thighs to neighbours 2 mm apart at the midline: torn when the legs
            // part)
            readonly int[] _kI = new int[4]; readonly float[] _kD = new float[4];
            public bool Nearest(Vector3 q, float max, int mask, Dictionary<Transform, float> acc)
            {
                int cx = Mathf.FloorToInt(q.x / _cell), cy = Mathf.FloorToInt(q.y / _cell), cz = Mathf.FloorToInt(q.z / _cell);
                int r = Mathf.CeilToInt(max / _cell);
                int found = 0; float max2 = max * max;
                for (int x = cx - r; x <= cx + r; x++)
                    for (int y = cy - r; y <= cy + r; y++)
                        for (int z = cz - r; z <= cz + r; z++)
                        {
                            if (!_grid.TryGetValue(Key(x, y, z), out var l)) continue;
                            foreach (var i in l)
                            {
                                if ((mask & (1 << _r[i])) == 0) continue;
                                float dd = (_p[i] - q).sqrMagnitude;
                                if (dd > max2) continue;
                                if (found < 4) { _kI[found] = i; _kD[found] = dd; found++; }
                                else
                                {
                                    int far = 0; for (int k = 1; k < 4; k++) if (_kD[k] > _kD[far]) far = k;
                                    if (dd < _kD[far]) { _kI[far] = i; _kD[far] = dd; }
                                }
                            }
                        }
                if (found == 0) return false;
                float sum = 0f;
                for (int k = 0; k < found; k++) { _kD[k] = 1f / (Mathf.Sqrt(_kD[k]) + 0.002f); sum += _kD[k]; }
                for (int k = 0; k < found; k++)
                {
                    var bb = _b[_kI[k]]; var ww = _w[_kI[k]]; float f = _kD[k] / sum;
                    for (int j = 0; j < 4; j++)
                    {
                        if (bb[j] == null || ww[j] <= 0f) continue;
                        acc.TryGetValue(bb[j], out var o); acc[bb[j]] = o + f * ww[j];
                    }
                }
                return acc.Count > 0;
            }
        }
    }
}
