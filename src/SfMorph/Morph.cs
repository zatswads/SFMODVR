using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace SfMorph
{
    // A converted character (tools/pipeline): BepInEx\config\SfMorphs\morphs\<name>\
    //   character.txt   name, source, the meshes (name + vertex count), her skin meshes, baked textures
    //   <mesh>.morph.txt  per-vertex position and normal deltas in her bind space, game vertex order
    //   joints.txt      per bone: bind-space offset of the joint (the shape moves where limbs bend)
    //   tex_*.jpg/png   VaM's skin baked onto her UV layout; thumb.jpg for the gallery
    internal class MorphSet
    {
        public string Name, Dir, Source = "?";
        public bool TwoFrames;   // character.txt 'frames<TAB>body face': separate Body and Face strength (the pipeline wrote head weights)
        public readonly Dictionary<string, int> Meshes = new Dictionary<string, int>();
        public readonly HashSet<string> Skin = new HashSet<string>();
        public readonly HashSet<string> Hide = new HashSet<string>();     // original mesh names to switch off while she is active
        public readonly List<string[]> Textures = new List<string[]>();   // material, kind (albedo/normal), file
        Texture2D _thumb; bool _thumbTried;
        Dictionary<string, Vector3> _joints;
        readonly Dictionary<string, MeshMorph> _cache = new Dictionary<string, MeshMorph>();
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static MorphSet Read(string dir)
        {
            var path = Path.Combine(dir, "character.txt");
            if (!File.Exists(path)) return null;
            var s = new MorphSet { Dir = dir, Name = Path.GetFileName(dir) };
            foreach (var raw in File.ReadAllLines(path))
            {
                var f = raw.Split('\t');
                if (f.Length < 2 || f[0].StartsWith("#")) continue;
                int n;
                switch (f[0])
                {
                    case "name": s.Name = f[1]; break;
                    case "source": s.Source = f[1]; break;
                    case "mesh": if (f.Length > 2 && int.TryParse(f[2], out n)) s.Meshes[f[1]] = n; break;
                    case "skin": s.Skin.Add(f[1]); break;
                    case "hide": s.Hide.Add(f[1]); break;
                    case "frames": s.TwoFrames = f[1].IndexOf("body", StringComparison.OrdinalIgnoreCase) >= 0 && f[1].IndexOf("face", StringComparison.OrdinalIgnoreCase) >= 0; break;
                    case "texture": if (f.Length > 3) s.Textures.Add(new[] { f[1], f[2], f[3] }); break;
                }
            }
            return s;
        }

        public Texture2D Thumb
        {
            get
            {
                if (_thumbTried) return _thumb;
                _thumbTried = true;
                var p = Path.Combine(Dir, "thumb.jpg");
                if (File.Exists(p)) { _thumb = new Texture2D(2, 2); _thumb.LoadImage(File.ReadAllBytes(p), true); }
                return _thumb;
            }
        }

        public MeshMorph Mesh(string name)
        {
            MeshMorph m;
            if (_cache.TryGetValue(name, out m)) return m;
            m = MeshMorph.Read(Path.Combine(Dir, name + ".morph.txt"));
            _cache[name] = m;
            return m;
        }

        public Dictionary<string, Vector3> Joints
        {
            get
            {
                if (_joints != null) return _joints;
                _joints = new Dictionary<string, Vector3>();
                var p = Path.Combine(Dir, "joints.txt");
                if (File.Exists(p))
                    foreach (var raw in File.ReadAllLines(p))
                    {
                        var f = raw.Split('\t');
                        if (f.Length < 4 || f[0].StartsWith("#")) continue;
                        var o = f[3].Split(' ');
                        float x, y, z;
                        if (o.Length == 3 && float.TryParse(o[0], NumberStyles.Float, Inv, out x) && float.TryParse(o[1], NumberStyles.Float, Inv, out y)
                            && float.TryParse(o[2], NumberStyles.Float, Inv, out z)) _joints[f[0]] = new Vector3(x, y, z);
                    }
                return _joints;
            }
        }

        public string TexturePath(string file) { return Path.Combine(Dir, file); }

        // Her whole fitted skin + bind poses as the pipeline wrote them (skin.txt, binds.txt): what clothes and outfit
        // body variants follow, whichever of her meshes the scene uses. Null for characters made before 0.6.2.
        SkinRef _ref; bool _refTried;
        public SkinRef Reference
        {
            get
            {
                if (_refTried) return _ref;
                _refTried = true;
                try { _ref = SkinRef.Load(Path.Combine(Dir, "skin.txt"), Path.Combine(Dir, "binds.txt")); }
                catch (Exception e) { Plugin.Log.LogWarning("Couldn't read the skin reference of " + Name + ": " + e.Message); }
                return _ref;
            }
        }

        // ---- the gallery: every folder with a character.txt ----

        static List<MorphSet> _all;
        public static string Root { get { return Path.Combine(Store.Dir, "morphs"); } }

        public static List<MorphSet> All
        {
            get
            {
                if (_all != null) return _all;
                _all = new List<MorphSet>();
                if (Directory.Exists(Root))
                    foreach (var d in Directory.GetDirectories(Root))
                    {
                        try { var s = Read(d); if (s != null) _all.Add(s); }
                        catch (Exception e) { Plugin.Log.LogWarning("Couldn't read character " + d + ": " + e.Message); }
                    }
                _all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                return _all;
            }
        }

        public static void Rescan() { _all = null; }

        public static MorphSet Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var s in All) if (s.Name == name) return s;
            return null;
        }
    }

    internal class MeshMorph
    {
        public Vector3[] D, N;
        public float[] H;   // head weight 0..1 per vertex (7th value of a line); null when the file has none (single-frame character)

        public static MeshMorph Read(string path)
        {
            if (!File.Exists(path)) return null;
            var lines = File.ReadAllLines(path);
            int n = -1, i = 0;
            for (; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("vertices ")) { n = int.Parse(lines[i].Substring(9), CultureInfo.InvariantCulture); i++; break; }
            }
            if (n < 0 || lines.Length - i < n) throw new Exception(Path.GetFileName(path) + ": truncated");
            var m = new MeshMorph { D = new Vector3[n], N = new Vector3[n] };
            var inv = CultureInfo.InvariantCulture;
            var h = new float[n]; bool allH = n > 0;
            for (int k = 0; k < n; k++)
            {
                var f = lines[i + k].Split(' ');
                m.D[k] = new Vector3(float.Parse(f[0], inv), float.Parse(f[1], inv), float.Parse(f[2], inv));
                if (f.Length >= 6) m.N[k] = new Vector3(float.Parse(f[3], inv), float.Parse(f[4], inv), float.Parse(f[5], inv));
                if (f.Length >= 7) h[k] = Mathf.Clamp01(float.Parse(f[6], inv)); else allH = false;
            }
            if (allH) m.H = h;
            return m;
        }

        // The shape as Unity blendshapes on a clone. With head weights: shape 0 'body' = D*(1-H), shape 1 'face' = D*H
        // (shape 0 stays the body, which SoftZones reads); without: one shape. Returns whether it has two.
        public static bool AddFrames(Mesh clone, string name, Vector3[] d, Vector3[] n, float[] h)
        {
            if (h == null || h.Length != d.Length) { clone.AddBlendShapeFrame("SfMorph " + name, 100f, d, n, null); return false; }
            var bd = new Vector3[d.Length]; var bn = new Vector3[d.Length]; var fd = new Vector3[d.Length]; var fn = new Vector3[d.Length];
            for (int k = 0; k < d.Length; k++)
            {
                float hv = h[k];
                bd[k] = d[k] * (1f - hv); fd[k] = d[k] * hv;
                if (n != null && k < n.Length) { bn[k] = n[k] * (1f - hv); fn[k] = n[k] * hv; }
            }
            clone.AddBlendShapeFrame("SfMorph body", 100f, bd, bn, null);
            clone.AddBlendShapeFrame("SfMorph face", 100f, fd, fn, null);
            return true;
        }
    }

    // One character's converted look on the game's woman: readable clones of her meshes (built from MeshData:
    // the game's own meshes are GPU-only) carrying the shape as a blendshape, swapped into her renderers; her
    // clothes and any mesh the pipeline didn't convert get a blendshape that follows the nearest skin vertex
    // (pushed out where the shape would put skin through cloth); joints moved with matching bind poses; baked
    // textures in her skin materials. Built once per character/mesh set, then only the weight changes.
    internal class MorphState
    {
        class Slot
        {
            public Mesh Orig, Clone;
            public Matrix4x4[] Bind;       // the original bind poses
            public Matrix4x4 ToMesh;       // common bind space -> this mesh's space (identity for her own meshes)
            public string Kind;            // "morph" or "follow"
            public bool Two;               // the clone has two blendshapes: 0 = body, 1 = face (else one, driven by Face)
            public float Weight = -1f, Weight1 = -1f, BindWeight = -1f;   // last weights set on shapes 0 and 1
        }
        class Saved { public Texture Tex; public bool Keyword; }

        readonly Dictionary<SkinnedMeshRenderer, Slot> _slots = new Dictionary<SkinnedMeshRenderer, Slot>();
        readonly Dictionary<Mesh, Mesh> _clones = new Dictionary<Mesh, Mesh>();       // original -> clone, per set
        readonly Dictionary<Mesh, Matrix4x4[]> _origBind = new Dictionary<Mesh, Matrix4x4[]>();
        readonly Dictionary<string, Vector3> _eff = new Dictionary<string, Vector3>(); // bone -> bind offset actually used
        readonly Dictionary<Material, Dictionary<string, Saved>> _saved = new Dictionary<Material, Dictionary<string, Saved>>();
        readonly Dictionary<string, Texture2D> _tex = new Dictionary<string, Texture2D>();
        readonly HashSet<string> _badTex = new HashSet<string>();
        readonly Dictionary<Material, Dictionary<string, Color>> _savedCol = new Dictionary<Material, Dictionary<string, Color>>();
        readonly List<SkinnedMeshRenderer> _hidden = new List<SkinnedMeshRenderer>();   // renderers we switched off ('hide' lines); only these get switched back on
        static readonly HashSet<string> _loggedProps = new HashSet<string>();
        readonly Dictionary<Transform, Vector3> _jointLocal = new Dictionary<Transform, Vector3>();
        static readonly HashSet<string> _loggedMats = new HashSet<string>();
        MorphSet _set;
        List<SkinnedMeshRenderer> _builtFor;
        bool _texOn;
        int _texKey;   // which skins are on: 1 = face, 2 = body
        float _nextCheck;
        string _status = "";
        public string Status
        {
            get { return _hidden.Count > 0 && _status.Length > 0 ? _status + ", " + _hidden.Count + " meshes hidden" : _status; }
            set { _status = value; }
        }

        public float JointWeight;   // what Body adds to the joints this frame
        public Dictionary<Transform, Vector3> JointLocal { get { return _jointLocal; } }

        // Called every frame (from the apply step) for each character.
        public void Update(Character c, bool apply)
        {
            var look = c.Look;
            var set = apply && look.Morph != null ? MorphSet.Find(look.Morph) : null;
            if (apply && look.Morph != null && set == null) Status = "off: no folder '" + look.Morph + "' in " + MorphSet.Root;
            float face = set != null ? Mathf.Clamp01(look.MorphFace) : 0f;
            float body = set != null ? Mathf.Clamp01(look.MorphBody) : 0f;
            if (set == null || (face <= 0f && body <= 0f)) { Release(); JointWeight = 0f; return; }

            string why = set != _set ? "character picked" : _builtFor == null ? "shape back on" : !SameList(_builtFor, c.Renderers) ? RendererChange(c) : null;
            if (why == null)
                foreach (var kv in _slots)  // the game swapped a mesh itself (mesh swaps are its customisation)
                    if (kv.Key != null && kv.Key.sharedMesh != kv.Value.Clone)
                    { why = "the game swapped " + kv.Key.name + "'s mesh to " + (kv.Key.sharedMesh != null ? kv.Key.sharedMesh.name : "none"); break; }
            bool rebuild = why != null;
            if (rebuild) _why = why;
            if (!rebuild && Time.unscaledTime >= _nextCheck)
            {
                _nextCheck = Time.unscaledTime + 2f;
                if (_texOn) ApplyTextures(c, set, _texKey);  // re-apply if DefaultSetting() put its own back
            }
            if (rebuild) Build(c, set);

            bool joints = look.MorphJoints;
            JointWeight = joints ? face : 0f;   // joint offsets are face joints only (eyes, jaw)
            foreach (var kv in _slots)
            {
                var r = kv.Key; var s = kv.Value;
                if (r == null || s.Clone == null || r.sharedMesh != s.Clone) continue;
                float bw = joints ? face : 0f;
                if (s.BindWeight != bw)
                {
                    SetBind(r, s, set, bw); s.BindWeight = bw;
                    r.sharedMesh = s.Clone;  // make sure the renderer picks up the new bind poses
                    s.Weight = -1f; s.Weight1 = -1f;
                }
                float w0 = s.Two ? body : face, w1 = s.Two ? face : 0f;   // two shapes: 0 body, 1 face; one shape: Face
                if (s.Weight != w0 || s.Weight1 != w1)
                {
                    r.SetBlendShapeWeight(0, w0 * 100f);
                    if (s.Two) r.SetBlendShapeWeight(1, w1 * 100f);
                    s.Weight = w0; s.Weight1 = w1;
                }
            }
            HideMeshes(c, set);
            // face and body skins switch separately (the face's texture alone gives the likeness on her body's skin)
            int key = (look.MorphSkin ? 1 : 0) | (look.MorphSkinBody ? 2 : 0);
            if (key != (_texOn ? _texKey : 0))
            {
                if (_texOn) RestoreTextures();
                _texOn = key != 0; _texKey = key;
                if (_texOn) ApplyTextures(c, set, key);
            }
        }

        string _why = "";

        // 'hide' lines: meshes of the game's woman the character replaces (by original mesh name) are switched off while
        // she is active. Only renderers we switched off are touched again; the game's own enabled state is left alone.
        void HideMeshes(Character c, MorphSet set)
        {
            for (int i = _hidden.Count - 1; i >= 0; i--)
            {
                var r = _hidden[i];
                if (r == null) { _hidden.RemoveAt(i); continue; }
                if (!c.Renderers.Contains(r) || !r.gameObject.activeInHierarchy || !set.Hide.Contains(OrigName(r)))
                { r.enabled = true; _hidden.RemoveAt(i); continue; }
                if (r.enabled) r.enabled = false;   // the game turned it back on
            }
            if (set.Hide.Count == 0) return;
            foreach (var r in c.Renderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || _hidden.Contains(r)) continue;
                var n = OrigName(r);
                if (n != null && set.Hide.Contains(n)) { r.enabled = false; _hidden.Add(r); }
            }
        }

        string OrigName(SkinnedMeshRenderer r)
        {
            Slot s;
            if (_slots.TryGetValue(r, out s) && s.Orig != null) return s.Orig.name;
            return r.sharedMesh != null ? r.sharedMesh.name : "";
        }

        void ShowHidden()
        {
            foreach (var r in _hidden) if (r != null) r.enabled = true;
            _hidden.Clear();
        }

        string RendererChange(Character c)
        {
            var add = new List<string>(); var rem = new List<string>();
            foreach (var r in c.Renderers) if (r != null && !_builtFor.Contains(r)) add.Add(r.name);
            foreach (var r in _builtFor) if (r == null || !c.Renderers.Contains(r)) rem.Add(r != null ? r.name : "(destroyed)");
            return "renderers " + (add.Count > 0 ? "+" + string.Join(" +", add.ToArray()) + " " : "") + (rem.Count > 0 ? "-" + string.Join(" -", rem.ToArray()) : "");
        }

        static bool SameList(List<SkinnedMeshRenderer> a, List<SkinnedMeshRenderer> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ---- building the clones ----

        void Build(Character c, MorphSet set)
        {
            if (set != _set)
            {
                ReleaseMeshes(true); RestoreTextures(); ShowHidden(); _texOn = false; _set = set;
                foreach (var t in _tex.Values) if (t != null) UnityEngine.Object.Destroy(t);
                _tex.Clear(); _badTex.Clear(); _texInfo.Clear();
            }
            _builtFor = new List<SkinnedMeshRenderer>(c.Renderers);
            // renderers that left her (hidden clothes, swapped parts) get their own mesh back
            var gone = new List<SkinnedMeshRenderer>();
            foreach (var kv in _slots) if (kv.Key == null || !_builtFor.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var r in gone)
            {
                var s = _slots[r];
                if (r != null && r.sharedMesh == s.Clone) r.sharedMesh = s.Orig;
                _slots.Remove(r);
            }
            var t0 = Time.realtimeSinceStartup;
            int morphed = 0, follow = 0, skipped = 0;
            var problems = new List<string>();
            // pass 1: her converted meshes (and the skin the clothes follow)
            var stored = set.Reference;
            var skin = stored ?? new SkinRef();   // older characters: the skin meshes present in the scene
            var pending = new List<KeyValuePair<SkinnedMeshRenderer, Mesh>>();
            foreach (var r in c.Renderers)
            {
                if (r == null) continue;
                Slot old;
                var orig = _slots.TryGetValue(r, out old) && (r.sharedMesh == old.Clone || r.sharedMesh == null) ? old.Orig : SoftZones.Original(r.sharedMesh);
                _slots.Remove(r);  // re-added below if it still gets a clone (a stale slot would rebuild every frame)
                if (orig == null) continue;
                if (r.sharedMesh != orig && old != null && r.sharedMesh == old.Clone) r.sharedMesh = orig;
                int count;
                if (!set.Meshes.TryGetValue(orig.name, out count)) { pending.Add(new KeyValuePair<SkinnedMeshRenderer, Mesh>(r, orig)); continue; }
                if (count != orig.vertexCount) { problems.Add(orig.name + " has " + orig.vertexCount + " vertices, the morph " + count); skipped++; continue; }
                MeshMorph mm;
                try { mm = set.Mesh(orig.name); }
                catch (Exception e) { problems.Add(e.Message); skipped++; continue; }
                if (mm == null) { problems.Add(orig.name + ": no morph file"); skipped++; continue; }
                if (mm.D.Length != orig.vertexCount) { problems.Add(orig.name + ": morph file has " + mm.D.Length + " vertices"); skipped++; continue; }
                var d = Data(orig, problems);
                if (d == null) { skipped++; continue; }
                Mesh clone;
                if (!_clones.TryGetValue(orig, out clone))
                {
                    clone = Clone(orig, d);
                    MeshMorph.AddFrames(clone, set.Name, mm.D, mm.N, mm.H);
                    _clones[orig] = clone;
                }
                Use(r, orig, clone, d, Matrix4x4.identity, "morph");
                if (stored == null && set.Skin.Contains(orig.name)) skin.Add(r, d, mm);
                morphed++;
            }
            // pass 2: clothes and anything not converted follow the nearest skin vertex
            if (stored == null && skin.Count > 0) skin.Index();
            foreach (var kv in pending)
            {
                var r = kv.Key; var orig = kv.Value;
                var d = Data(orig, problems);
                if (d == null) { skipped++; continue; }
                if (d.Weights == null || skin.Count == 0) { problems.Add(orig.name + ": not skinned / no skin to follow"); skipped++; continue; }
                Matrix4x4 toSkin;
                if (!skin.MapFrom(r, d, out toSkin)) { problems.Add(orig.name + ": shares no bone with her skin"); skipped++; continue; }
                Mesh clone;
                if (!_clones.TryGetValue(orig, out clone))
                {
                    clone = Clone(orig, d);
                    Vector3[] fd, fn, fh, fhn;
                    skin.Follow(d.Pos, d.Nrm, toSkin, out fd, out fn, out fh, out fhn);
                    if (fh != null)   // body part (with the push-out) and face part
                    {
                        clone.AddBlendShapeFrame("SfMorph body", 100f, fd, fn, null);
                        clone.AddBlendShapeFrame("SfMorph face", 100f, fh, fhn, null);
                    }
                    else clone.AddBlendShapeFrame("SfMorph " + set.Name, 100f, fd, fn, null);
                    _clones[orig] = clone;
                }
                Use(r, orig, clone, d, toSkin.inverse, "follow");
                follow++;
            }
            JointLocals(c, set);
            Status = set.Name + ": " + morphed + " meshes morphed, " + follow + " follow (clothes and the rest)" + (skipped > 0 ? ", " + skipped + " skipped (see the log)" : "")
                     + "; " + _jointLocal.Count + " joints moved, " + simulatedCount + " physics/driven bones keep their parent's";
            Plugin.Log.LogInfo("Character '" + set.Name + "' on '" + c.Key + "': " + morphed + " meshes morphed, " + follow + " follow the skin, "
                               + _jointLocal.Count + " joints, built in " + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("0") + " ms (" + _why + ")"
                               + (problems.Count > 0 ? "; skipped: " + string.Join("; ", problems.ToArray()) : ""));
        }

        // The mesh's data: from our clone when there is one (readable, fast), else read back from the GPU;
        // always with the original bind poses (the clone's are moved with the joints).
        MeshData Data(Mesh orig, List<string> problems)
        {
            MeshData d;
            if (_data.TryGetValue(orig, out d)) return d;
            string err;
            d = MeshData.Read(orig, out err);
            if (d == null) { problems.Add(orig.name + ": " + err); return null; }
            _data[orig] = d;
            if (_loggedLayout.Add(orig.name))
            {
                var sb = new StringBuilder("Mesh '" + orig.name + "' layout:");
                foreach (var a in orig.GetVertexAttributes()) sb.Append(' ').Append(a.attribute).Append('=').Append(a.format).Append('x').Append(a.dimension).Append("/s").Append(a.stream);
                Plugin.Log.LogInfo(sb.ToString());
            }
            return d;
        }
        readonly Dictionary<Mesh, MeshData> _data = new Dictionary<Mesh, MeshData>();
        static readonly HashSet<string> _loggedLayout = new HashSet<string>();

        void Use(SkinnedMeshRenderer r, Mesh orig, Mesh clone, MeshData d, Matrix4x4 toMesh, string kind)
        {
            _slots[r] = new Slot { Orig = orig, Clone = clone, Bind = d.Bind, ToMesh = toMesh, Kind = kind, Two = clone.blendShapeCount >= 2 };
            r.sharedMesh = clone;
        }

        internal static Mesh Clone(Mesh orig, MeshData d)
        {
            var m = new Mesh { name = orig.name + " (SfMorph)" };
            if (d.Pos.Length > 65535) m.indexFormat = IndexFormat.UInt32;
            m.vertices = d.Pos;
            if (d.Nrm != null && d.Nrm.Length == d.Pos.Length) m.normals = d.Nrm;
            if (d.Tan != null && d.Tan.Length == d.Pos.Length) m.tangents = d.Tan;
            if (d.Uv != null && d.Uv.Length == d.Pos.Length) m.uv = d.Uv;
            for (int c = 1; c < 4; c++) if (d.Uvs[c] != null && d.Uvs[c].Count == d.Pos.Length) m.SetUVs(c, d.Uvs[c]);
            if (d.Col != null && d.Col.Length == d.Pos.Length) m.colors = d.Col;
            m.subMeshCount = d.Sub.Count;
            for (int s = 0; s < d.Sub.Count; s++) m.SetTriangles(d.Sub[s], s, false);
            if (d.Weights != null && d.Weights.Length == d.Pos.Length) m.boneWeights = d.Weights;
            m.bindposes = d.Bind;
            m.bounds = orig.bounds;
            return m;
        }

        // ---- joints: bone moves (applied by Body through its trackers) and matching bind poses ----

        // Each bone's offset in its parent's local space, from the bind-space offsets: in the bind pose the moved
        // skeleton plus the new bind poses give exactly the blendshape's shape, and limbs bend at the new joints.
        // DynamicBone (breasts, hair) resets the bones it simulates to their initial offsets every frame and builds its
        // chain from them, so those bones aren't moved: they keep their parent's offset, and so do their bind poses.
        void JointLocals(Character c, MorphSet set)
        {
            _jointLocal.Clear(); _eff.Clear();
            var j = set.Joints;
            var simulated = new HashSet<Transform>();
            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (mb == null || mb.GetType().Name != "DynamicBone") continue;
                var f = mb.GetType().GetField("m_Root");
                var root = f != null ? f.GetValue(mb) as Transform : null;
                if (root == null || !root.IsChildOf(c.Root)) continue;
                foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t != root) simulated.Add(t);
            }
            // A bone hanging under a transform that isn't a skin bone (face rig 'Facial', eyelid and thigh-skin drivers)
            // has no bind pose for its parent: its offset can't be put into the parent's space reliably, and the
            // driver may be moved by the game. Such bones aren't moved; they keep their ancestors' offset. The
            // same goes for bones the physics simulates.
            var skinSet = new HashSet<Transform>(c.BoneList);
            var fixedBones = new HashSet<Transform>();
            foreach (var t in c.BoneList)  // parents first
            {
                if (t == null) continue;
                Vector3 o;
                bool aboveSkin = true;   // parent chain has no skin bone: the static rig root (DeformationSystem)
                for (var a = t.parent; a != null; a = a.parent) if (skinSet.Contains(a)) { aboveSkin = false; break; }
                bool driven = t.parent != null && !skinSet.Contains(t.parent) && !aboveSkin;
                if (simulated.Contains(t) || driven) fixedBones.Add(t);
                if (!fixedBones.Contains(t) && j.TryGetValue(t.name, out o)) _eff[t.name] = o;
                else _eff[t.name] = Inherited(t);   // moves with its ancestors
            }
            // parent bind poses from her converted meshes' original bind poses (not clothes, not our moved ones)
            var bindOf = new Dictionary<Transform, Matrix4x4>();
            foreach (var kv in _slots)
            {
                if (kv.Key == null || kv.Value.Kind != "morph") continue;
                var bones = kv.Key.bones;
                for (int i = 0; i < bones.Length && i < kv.Value.Bind.Length; i++)
                    if (bones[i] != null && !bindOf.ContainsKey(bones[i])) bindOf[bones[i]] = kv.Value.Bind[i];
            }
            Transform meshSpace = null;
            foreach (var r in c.Renderers) if (r != null) { meshSpace = r.transform; break; }
            foreach (var t in c.BoneList)
            {
                Vector3 dt, dp;
                if (t == null || fixedBones.Contains(t) || !_eff.TryGetValue(t.name, out dt)) continue;
                var p = t.parent;
                if (p == null) continue;
                if (!_eff.TryGetValue(p.name, out dp)) dp = Inherited(p);
                var delta = dt - dp;
                if (delta.sqrMagnitude < 1e-10f) continue;
                Matrix4x4 bp;
                Vector3 local;
                if (bindOf.TryGetValue(p, out bp) || c.Bind.TryGetValue(p, out bp)) local = bp.MultiplyVector(delta);          // bind pose: mesh -> parent
                else if (meshSpace != null) local = p.InverseTransformVector(meshSpace.TransformVector(delta)); // static rig root above the skeleton
                else continue;
                _jointLocal[t] = local;
            }
            simulatedCount = fixedBones.Count;
        }
        int simulatedCount;

        // The offset a transform gets from its nearest ancestor that has one (it moves along with it).
        Vector3 Inherited(Transform t)
        {
            Vector3 o;
            for (var a = t.parent; a != null; a = a.parent) if (_eff.TryGetValue(a.name, out o)) return o;
            return Vector3.zero;
        }

        void SetBind(SkinnedMeshRenderer r, Slot s, MorphSet set, float w)
        {
            var j = _eff;
            var bones = r.bones;
            var cur = s.Clone.bindposes;   // soft zones may have appended helper bones: keep theirs (they follow BindVersion)
            var bp = new Matrix4x4[Mathf.Max(cur.Length, s.Bind.Length)];
            Array.Copy(cur, bp, cur.Length);
            Array.Copy(s.Bind, bp, s.Bind.Length);
            if (w > 0f)
                for (int i = 0; i < s.Bind.Length && i < bones.Length; i++)
                {
                    Vector3 o;
                    if (bones[i] == null || !j.TryGetValue(bones[i].name, out o)) continue;
                    bp[i] = bp[i] * Matrix4x4.Translate(-(s.ToMesh.MultiplyVector(o) * w));
                }
            s.Clone.bindposes = bp;
            BindVersion++;
        }
        public static int BindVersion;   // bumped whenever a clone's bind poses move (SoftZones re-derives its helpers' then)

        // ---- textures ----

        static readonly string[] AlbedoProps = { "_ColorMap", "_BaseColorMap", "_BaseMap", "_MainTex", "_AlbedoMap", "_Albedo", "_DiffuseMap", "_Diffuse" };
        static readonly string[] NormalProps = { "_NormalMap", "_BumpMap" };
        static readonly string[] SmoothProps = { "_SmoothnessMap" };

        // Normal maps stay uncompressed (DXT5 normals band on skin); a later config may turn compression on.
        public static bool CompressNormals = false;
        // The character's gloss as the skin's smoothness map: off until we know how the game's skin shader reads
        // _SmoothnessMap (VaM gloss maps are dark; applied blindly they can make her skin look dull).
        public static bool UseSmoothness = false;
        readonly Dictionary<string, string> _texInfo = new Dictionary<string, string>();   // cache key -> "8192², 2.1 s"
        double _loadMs;                                                                     // load+compress time of this ApplyTextures call
        static readonly HashSet<string> _loggedNoProp = new HashSet<string>();

        void ApplyTextures(Character c, MorphSet set, int key)
        {
            var done = new List<string>();
            _loadMs = 0;
            foreach (var r in c.Renderers)
            {
                if (r == null) continue;
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null) continue;
                    var mname = mat.name.Replace(" (Instance)", "").Trim();
                    foreach (var t in set.Textures)
                    {
                        if (((t[0].IndexOf("face", StringComparison.OrdinalIgnoreCase) >= 0) ? 1 : 2) is int part && (key & part) == 0) continue;
                        if (t[0] != mname) continue;
                        if (_loggedMats.Add(mname))
                            Plugin.Log.LogInfo("Material '" + mname + "' (" + mat.shader.name + ") textures: " + string.Join(", ", mat.GetTexturePropertyNames()));
                        string kind = t[1] == "normal" ? "normal" : t[1] == "smooth" ? "smooth" : "albedo";
                        if (kind == "smooth" && !UseSmoothness) continue;
                        bool normal = kind == "normal";
                        string prop = null;
                        foreach (var pn in normal ? NormalProps : kind == "smooth" ? SmoothProps : AlbedoProps) if (mat.HasProperty(pn)) { prop = pn; break; }
                        if (prop == null)
                        {
                            if (_loggedNoProp.Add(mname + "|" + kind)) Plugin.Log.LogInfo("Material '" + mname + "' has no property for the " + kind + " texture " + t[2] + "; skipped");
                            continue;
                        }
                        string info;
                        var tex = Load(set, t[2], kind, out info);
                        if (_loggedProps.Add(mname)) LogProps(mname, mat);
                        if (tex != null && kind == "albedo") WhiteTint(mat);
                        if (tex == null || mat.GetTexture(prop) == tex) continue;
                        Dictionary<string, Saved> sv;
                        if (!_saved.TryGetValue(mat, out sv)) { sv = new Dictionary<string, Saved>(); _saved[mat] = sv; }
                        if (!sv.ContainsKey(prop)) sv[prop] = new Saved { Tex = mat.GetTexture(prop), Keyword = mat.IsKeywordEnabled("_NORMALMAP") };
                        mat.SetTexture(prop, tex);
                        if (normal) mat.EnableKeyword("_NORMALMAP");
                        done.Add(mname + "." + prop + " [" + kind + "] (" + info + ")");
                    }
                }
            }
            if (done.Count > 0) Plugin.Log.LogInfo("Character '" + set.Name + "' textures: " + string.Join(", ", done.ToArray()));
            if (_loadMs > 3000.0)
                Plugin.Log.LogInfo("Loading the textures took " + (_loadMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)
                                   + " s; the bake size can be lowered with run.ps1 -TextureSize 4096");
        }

        static readonly string[] TintProps = { "_SkinColor", "_BaseColor", "_Color" };

        // The skin shader multiplies the albedo by its own tint: with the baked skin on, the tint is white (originals come back with the textures).
        void WhiteTint(Material mat)
        {
            foreach (var pn in TintProps)
            {
                if (!mat.HasProperty(pn)) continue;
                Dictionary<string, Color> sc;
                if (!_savedCol.TryGetValue(mat, out sc)) { sc = new Dictionary<string, Color>(); _savedCol[mat] = sc; }
                var cur = mat.GetColor(pn);
                if (!sc.ContainsKey(pn)) sc[pn] = cur;
                if (cur != Color.white) mat.SetColor(pn, Color.white);
            }
        }

        static string F(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        // Once per material: every float and colour property, to tune the look later.
        static void LogProps(string mname, Material mat)
        {
            try
            {
                var sh = mat.shader;
                var sb = new StringBuilder("Material '" + mname + "' (" + sh.name + ") properties:");
                int n = sh.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    var type = sh.GetPropertyType(i);
                    var pn = sh.GetPropertyName(i);
                    if (type == ShaderPropertyType.Float || type == ShaderPropertyType.Range) sb.Append(' ').Append(pn).Append('=').Append(F(mat.GetFloat(pn)));
                    else if (type == ShaderPropertyType.Color)
                    {
                        var col = mat.GetColor(pn);
                        sb.Append(' ').Append(pn).Append("=(").Append(F(col.r)).Append(',').Append(F(col.g)).Append(',').Append(F(col.b)).Append(',').Append(F(col.a)).Append(')');
                    }
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't list the properties of '" + mname + "': " + e.Message); }
        }

        // kind: albedo (sRGB, DXT5), smooth (linear, DXT5), normal (linear, uncompressed unless CompressNormals).
        // Loaded readable, compressed, then made non-readable. Any failure skips the texture (never applied).
        Texture2D Load(MorphSet set, string file, string kind, out string info)
        {
            var key = set.Dir + "|" + file;
            Texture2D t;
            info = "";
            if (_tex.TryGetValue(key, out t) && t != null)
            {
                string cached;
                if (_texInfo.TryGetValue(key, out cached)) info = cached;
                return t;
            }
            if (_badTex.Contains(key)) return null;
            var path = set.TexturePath(file);
            if (!File.Exists(path)) { _badTex.Add(key); Plugin.Log.LogWarning("Missing " + path); return null; }
            bool linear = kind != "albedo";
            bool compress = kind == "normal" ? CompressNormals : true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            t = null;
            try
            {
                t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = "SfMorph " + file, anisoLevel = 8 };
                if (!t.LoadImage(File.ReadAllBytes(path), false))   // readable: Compress needs the pixels
                {
                    UnityEngine.Object.Destroy(t); _badTex.Add(key);
                    Plugin.Log.LogWarning("Couldn't decode " + path + "; skipped"); return null;
                }
                string note = "";
                if (compress)
                {
                    if (t.width % 4 == 0 && t.height % 4 == 0)
                    {
                        t.Compress(true);
                        if (t.format == TextureFormat.RGBA32) note = ", not compressed";
                    }
                    else note = ", not compressed (size not a multiple of 4)";
                }
                t.Apply(false, true);   // upload and drop the CPU copy
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                _loadMs += ms;
                info = (t.width == t.height ? t.width + "²" : t.width + "x" + t.height) + ", "
                       + (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s" + note;
                if (note.Length > 0) Plugin.Log.LogWarning(file + ": " + info);
            }
            catch (Exception e)
            {
                if (t != null) UnityEngine.Object.Destroy(t);
                _badTex.Add(key);
                Plugin.Log.LogWarning("Couldn't load/compress " + path + " (" + e.Message + "); skipped");
                info = "";
                return null;
            }
            _tex[key] = t;
            _texInfo[key] = info;
            return t;
        }

        void RestoreTextures()
        {
            foreach (var kv in _saved)
            {
                if (kv.Key == null) continue;
                foreach (var p in kv.Value)
                {
                    kv.Key.SetTexture(p.Key, p.Value.Tex);
                    if (p.Key == "_NormalMap" || p.Key == "_BumpMap") { if (p.Value.Keyword) kv.Key.EnableKeyword("_NORMALMAP"); else kv.Key.DisableKeyword("_NORMALMAP"); }
                }
            }
            _saved.Clear();
            foreach (var kv in _savedCol)
            {
                if (kv.Key == null) continue;
                foreach (var p in kv.Value) kv.Key.SetColor(p.Key, p.Value);
            }
            _savedCol.Clear();
        }

        // ---- giving everything back ----

        public void Release()
        {
            ShowHidden();
            if (_slots.Count == 0 && _saved.Count == 0 && _savedCol.Count == 0) return;
            ReleaseMeshes(false);
            RestoreTextures();
            _texOn = false;
            _builtFor = null;
            Status = "";
        }

        void ReleaseMeshes(bool destroy)
        {
            foreach (var kv in _slots)
                if (kv.Key != null && kv.Key.sharedMesh == kv.Value.Clone) kv.Key.sharedMesh = kv.Value.Orig;
            _slots.Clear();
            _jointLocal.Clear();
            if (!destroy) return;
            foreach (var m in _clones.Values) if (m != null) UnityEngine.Object.Destroy(m);
            _clones.Clear();
            _data.Clear();
        }
    }

    // Her skin in bind space (positions, normals, morph deltas) with a uniform grid for nearest-vertex lookups,
    // so clothes can follow the shape.
    internal class SkinRef
    {
        readonly List<Vector3> _p = new List<Vector3>(), _n = new List<Vector3>(), _d = new List<Vector3>(), _dn = new List<Vector3>();
        readonly List<float> _h = new List<float>();   // head weight per skin point; only complete when every point has one
        public bool HasH { get { return _p.Count > 0 && _h.Count == _p.Count; } }
        readonly Dictionary<Transform, Matrix4x4> _bind = new Dictionary<Transform, Matrix4x4>();
        Dictionary<long, List<int>> _grid;
        const float Cell = 0.02f;
        public int Count { get { return _p.Count; } }

        readonly Dictionary<string, Matrix4x4> _bindByName = new Dictionary<string, Matrix4x4>();

        public static SkinRef Load(string skinPath, string bindPath)
        {
            if (!File.Exists(skinPath) || !File.Exists(bindPath)) return null;
            var inv = CultureInfo.InvariantCulture;
            var s = new SkinRef();
            var lines = File.ReadAllLines(skinPath);
            int i = 0, n = -1;
            for (; i < lines.Length; i++) if (lines[i].StartsWith("vertices ")) { n = int.Parse(lines[i].Substring(9), inv); i++; break; }
            if (n < 0 || lines.Length - i < n) throw new Exception("skin.txt truncated");
            for (int k = 0; k < n; k++)
            {
                var f = lines[i + k].Split(' ');
                Func<int, Vector3> v3 = o => new Vector3(float.Parse(f[o], inv), float.Parse(f[o + 1], inv), float.Parse(f[o + 2], inv));
                s._p.Add(v3(0)); s._n.Add(v3(3)); s._d.Add(v3(6)); s._dn.Add(v3(9));
                if (f.Length >= 13 && s._h.Count == k) s._h.Add(Mathf.Clamp01(float.Parse(f[12], inv)));   // absent on any line: single-frame character
            }
            foreach (var raw in File.ReadAllLines(bindPath))
            {
                var f = raw.Split('\t');
                if (f.Length < 2 || f[0].StartsWith("#")) continue;
                var v = f[1].Split(' ');
                if (v.Length != 16) continue;
                var m = new Matrix4x4();
                for (int k = 0; k < 16; k++) m[k / 4, k % 4] = float.Parse(v[k], inv);
                s._bindByName[f[0]] = m;
            }
            s.Index();
            return s;
        }

        public void Add(SkinnedMeshRenderer r, MeshData d, MeshMorph m)
        {
            for (int i = 0; i < d.Pos.Length; i++)
            {
                _p.Add(d.Pos[i]); _n.Add(d.Nrm != null ? d.Nrm[i] : Vector3.up); _d.Add(m.D[i]); _dn.Add(m.N[i]);
                if (m.H != null && _h.Count == _p.Count - 1) _h.Add(m.H[i]);
            }
            var bones = r.bones;
            for (int i = 0; i < bones.Length && i < d.Bind.Length; i++) if (bones[i] != null && !_bind.ContainsKey(bones[i])) _bind[bones[i]] = d.Bind[i];
        }

        static long Key(int x, int y, int z) { return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF); }

        public void Index()
        {
            _grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < _p.Count; i++)
            {
                var k = Key(Mathf.FloorToInt(_p[i].x / Cell), Mathf.FloorToInt(_p[i].y / Cell), Mathf.FloorToInt(_p[i].z / Cell));
                List<int> l;
                if (!_grid.TryGetValue(k, out l)) { l = new List<int>(); _grid[k] = l; }
                l.Add(i);
            }
        }

        // The clothing mesh's space -> skin space, through the bone it is most weighted to that the skin also uses.
        public bool MapFrom(SkinnedMeshRenderer r, MeshData d, out Matrix4x4 toSkin)
        {
            toSkin = Matrix4x4.identity;
            var bones = r.bones;
            var sum = new float[bones.Length];
            foreach (var w in d.Weights)
            {
                if (w.boneIndex0 < sum.Length) sum[w.boneIndex0] += w.weight0;
                if (w.boneIndex1 < sum.Length) sum[w.boneIndex1] += w.weight1;
            }
            int best = -1;
            for (int i = 0; i < bones.Length && i < d.Bind.Length; i++)
                if (bones[i] != null && (_bind.ContainsKey(bones[i]) || _bindByName.ContainsKey(bones[i].name)) && (best < 0 || sum[i] > sum[best])) best = i;
            if (best < 0) return false;
            Matrix4x4 b;
            if (!_bind.TryGetValue(bones[best], out b)) b = _bindByName[bones[best].name];
            toSkin = b.inverse * d.Bind[best];
            return true;
        }

        // Per vertex: the weighted delta of the nearest skin vertices; then, where the shape would put the skin through
        // the cloth (cloth was outside the skin and isn't any more), pushed out along the new skin normal.
        // With head weights (HasH) the result splits: outD/outN is the body part (plus the push-out), faceD/faceN the face
        // part (the interpolated h share of the delta); both null-free sum to the single-frame result. Without: faceD/faceN null.
        public void Follow(Vector3[] pos, Vector3[] nrm, Matrix4x4 toSkin, out Vector3[] outD, out Vector3[] outN, out Vector3[] faceD, out Vector3[] faceN)
        {
            var back = toSkin.inverse;
            bool split = HasH;
            outD = new Vector3[pos.Length];
            outN = new Vector3[pos.Length];
            faceD = split ? new Vector3[pos.Length] : null;
            faceN = split ? new Vector3[pos.Length] : null;
            var idx = new int[4]; var dist = new float[4];
            for (int v = 0; v < pos.Length; v++)
            {
                var p = toSkin.MultiplyPoint3x4(pos[v]);
                int found = Nearest(p, idx, dist);
                if (found == 0) continue;
                Vector3 dsum = Vector3.zero, nsum = Vector3.zero; float wsum = 0f, hsum = 0f;
                for (int k = 0; k < found; k++)
                {
                    float w = 1f / (dist[k] + 0.002f); w *= w;
                    dsum += _d[idx[k]] * w; nsum += _dn[idx[k]] * w; wsum += w;
                    if (split) hsum += _h[idx[k]] * w;
                }
                var dd = dsum / wsum;
                float hv = split ? Mathf.Clamp01(hsum / wsum) : 0f;
                Vector3 nDelta = Vector3.zero;
                if (nrm != null && v < nrm.Length)  // the skin's normal change, carried over (outfit body variants are skin too)
                {
                    var fn0 = toSkin.MultiplyVector(nrm[v]).normalized;
                    var fn1 = (fn0 + nsum / wsum).normalized;
                    nDelta = back.MultiplyVector(fn1).normalized * nrm[v].magnitude - nrm[v];
                }
                if (split) { faceN[v] = nDelta * hv; outN[v] = nDelta * (1f - hv); } else outN[v] = nDelta;
                int n0 = idx[0];
                var nb = _n[n0]; var pb = _p[n0];
                float h0 = Vector3.Dot(p - pb, nb);
                var nb1 = (nb + _dn[n0]).normalized; var pb1 = pb + _d[n0];
                var p1 = p + dd;
                float h1 = Vector3.Dot(p1 - pb1, nb1);
                float want = h0 > 0f ? Mathf.Min(h0, 0.003f) : float.NegativeInfinity;
                var push = Vector3.zero;
                if (h1 < want) push = nb1 * (want - h1);
                if (split) { faceD[v] = back.MultiplyVector(dd * hv); outD[v] = back.MultiplyVector(dd * (1f - hv) + push); }   // the push-out goes with the body
                else outD[v] = back.MultiplyVector(dd + push);
            }
        }

        int Nearest(Vector3 p, int[] idx, float[] dist)
        {
            int n = 0;
            for (int k = 0; k < idx.Length; k++) dist[k] = float.MaxValue;
            int cx = Mathf.FloorToInt(p.x / Cell), cy = Mathf.FloorToInt(p.y / Cell), cz = Mathf.FloorToInt(p.z / Cell);
            for (int ring = 1; ring <= 4 && (n == 0 || ring <= 2); ring++)
            {
                n = 0;
                for (int k = 0; k < idx.Length; k++) dist[k] = float.MaxValue;
                for (int x = cx - ring; x <= cx + ring; x++)
                    for (int y = cy - ring; y <= cy + ring; y++)
                        for (int z = cz - ring; z <= cz + ring; z++)
                        {
                            List<int> l;
                            if (!_grid.TryGetValue(Key(x, y, z), out l)) continue;
                            foreach (var i in l)
                            {
                                float dd = (_p[i] - p).sqrMagnitude;
                                if (dd >= dist[idx.Length - 1]) continue;
                                int at = idx.Length - 1;
                                while (at > 0 && dist[at - 1] > dd) { dist[at] = dist[at - 1]; idx[at] = idx[at - 1]; at--; }
                                dist[at] = dd; idx[at] = i;
                                if (n < idx.Length) n++;
                            }
                        }
                if (n > 0 && dist[0] <= (ring * Cell) * (ring * Cell)) break;
            }
            for (int k = 0; k < n; k++) dist[k] = Mathf.Sqrt(dist[k]);
            return n;
        }
    }
}
