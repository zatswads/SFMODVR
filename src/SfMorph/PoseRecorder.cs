using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.SceneManagement;

namespace SfMorph
{
    // Records the game's own animation of one character, for the pipeline's pose check (does the Python
    // skinning formula reproduce what the game draws?). Samples every skin bone's matrix in the character
    // root's space once per 1/fps s, in a PlayerLoop step right before the game skins the meshes (the pose
    // every renderer is about to draw), and bakes the primary renderer once as ground truth.
    //
    // File: BepInEx\config\SfMorphs\poses\<scene>-<yyyyMMdd-HHmmss>.txt (text, invariant culture, '\n'),
    // plus <same name>.bake.obj when BakeMesh works. Lines:
    //   # ...                comment
    //   key/scene/date/fps/seconds/rootScale/flags/primary/primaryToRoot/bake   header, "name value..."
    //   bones <N>            then N lines "bone <index> <name>" (frame matrices follow this order)
    //   frames <F>
    //   then per frame:  "frame <i> <Time.time> <seconds since the first frame>"
    //                    "M <12*N floats>": per bone rows 0-2 of M = root.worldToLocal * bone.localToWorld,
    //                                       row-major M[r,c], row r = m[r,0] m[r,1] m[r,2] m[r,3];
    //                                       a destroyed bone is 12 zeros
    //                    "R <renderer name>\t<sharedMesh name>\t<enabled 0|1>\t<blendShapeCount>" per renderer
    //                    "end"
    internal static class PoseRecorder
    {
        internal struct RecordStep { }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static bool _on;
        static string _status = "";
        static Character _c;
        static float _seconds, _fps, _start, _nextAt, _startReal;
        static string _scene, _flags;
        static string _path;
        static List<Transform> _bones;
        static List<SkinnedMeshRenderer> _rends;
        static SkinnedMeshRenderer _primary;
        static Matrix4x4 _primaryToRoot;
        static Vector3 _rootScale;
        static string _key;
        static string _bakeNote;
        static StringBuilder _frames;
        static int _frameCount;

        public static bool Busy { get { return _on; } }
        public static string Status { get { return _status; } }

        // p: only for the header's flags (Apply, soft zones, Embody, physics hands); "?" when null.
        public static void Start(Character c, float seconds = 6f, float fps = 10f, Plugin p = null)
        {
            if (_on) return;
            try
            {
                if (c == null || c.Root == null) { _status = "off: no character"; return; }
                _c = c;
                _key = c.Key;
                _rootScale = c.Root.lossyScale;
                _seconds = Mathf.Max(0.1f, seconds);
                _fps = Mathf.Clamp(fps, 1f, 120f);
                _scene = SceneManager.GetActiveScene().name;
                _rends = new List<SkinnedMeshRenderer>();
                foreach (var r in c.Renderers) if (r != null) _rends.Add(r);
                _primary = Primary(_rends);
                _bones = CollectBones(c, _rends);
                if (_primary == null || _bones.Count == 0) { _status = "off: no skinned meshes or bones"; return; }
                _flags = Flags(c, p);
                _frames = new StringBuilder();
                _frameCount = 0;
                _bakeNote = null;
                _start = -1f;
                _startReal = Time.unscaledTime;
                _path = Path.Combine(Path.Combine(Store.Dir, "poses"), Store.Safe(_scene) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv) + ".txt");
                if (!Insert()) { _status = "off: couldn't join Unity's frame loop"; Clear(); return; }
                _on = true;
                _status = "recording " + _seconds.ToString("0.#", Inv) + " s (" + _bones.Count + " bones)...";
                Plugin.Log.LogInfo("Pose recording started: " + _bones.Count + " bones, " + _rends.Count + " renderers, primary " + _primary.name);
            }
            catch (Exception e)
            {
                _on = false;
                _status = "off: " + e.Message;
                Plugin.Log.LogError("Pose recorder couldn't start: " + e);
                try { Remove(); } catch { }
                Clear();
            }
        }

        // ---- the step ----

        static void Step()
        {
            if (!_on) return;
            try
            {
                if (_c == null || _c.Root == null) { Finish("the character went away"); return; }
                float t = Time.time;
                if (_start < 0f)
                {
                    _start = t;
                    _nextAt = t;
                    Bake();
                }
                if (t - _start >= _seconds && _frameCount > 0) { Finish(null); return; }
                // Time.time not advancing (paused) would wait forever: give up after a generous real-time limit.
                if (Time.unscaledTime - _startReal > _seconds * 4f + 10f) { Finish("timed out (game paused?)"); return; }
                if (t >= _nextAt - 1e-5f)
                {
                    Sample(t);
                    _nextAt += 1f / _fps;
                    if (_nextAt < t) _nextAt = t + 1f / _fps;   // fell behind (slow frame): skip, don't burst
                    _status = "recording " + _frameCount + " frames, " + Mathf.Max(0f, _seconds - (t - _start)).ToString("0.0", Inv) + " s left...";
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Pose recorder stopped: " + e);
                _status = "off: " + e.Message;
                _on = false;
                try { Remove(); } catch { }
                Clear();
            }
        }

        static void Sample(float t)
        {
            var sb = _frames;
            sb.Append("frame ").Append(_frameCount).Append(' ').Append(F(t)).Append(' ').Append(F(t - _start)).Append('\n');
            var w2l = _c.Root.worldToLocalMatrix;
            sb.Append('M');
            foreach (var b in _bones)
            {
                if (b == null) { for (int i = 0; i < 12; i++) sb.Append(" 0"); continue; }
                var m = w2l * b.localToWorldMatrix;
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 4; c++) sb.Append(' ').Append(F(m[r, c]));
            }
            sb.Append('\n');
            foreach (var r in _rends)
            {
                if (r == null) { sb.Append("R <destroyed>\t-\t0\t0\n"); continue; }
                int bs = 0;
                string mn = "-";
                try { var m = r.sharedMesh; if (m != null) { mn = m.name; bs = m.blendShapeCount; } } catch { }
                sb.Append("R ").Append(r.name).Append('\t').Append(mn).Append('\t').Append(r.enabled ? 1 : 0).Append('\t').Append(bs).Append('\n');
            }
            sb.Append("end\n");
            _frameCount++;
        }

        // BakeMesh of the primary renderer on the first frame: its vertices in the renderer's local space
        // (useScale true), next to the file. May throw or log an error for meshes without CPU data; then skipped.
        static void Bake()
        {
            string bakePath = Path.ChangeExtension(_path, ".bake.obj");
            Mesh baked = null;
            try
            {
                _primaryToRoot = _c.Root.worldToLocalMatrix * _primary.transform.localToWorldMatrix;
                baked = new Mesh();
                _primary.BakeMesh(baked, true);
                int n = baked.vertexCount;
                if (n == 0) throw new Exception("BakeMesh returned an empty mesh");
                var v = baked.vertices;
                Directory.CreateDirectory(Path.GetDirectoryName(bakePath));
                var sb = new StringBuilder(n * 32 + 512);
                sb.Append("# SfMorph pose recorder: BakeMesh(useScale: true) of ").Append(_primary.name).Append(" (mesh ").Append(_primary.sharedMesh != null ? _primary.sharedMesh.name : "?")
                  .Append("), vertices in the renderer's local space, vertex order of the game's mesh\n");
                sb.Append("# rendererToRoot (row-major 4x4, M[r,c]): ").Append(Mat16(_primaryToRoot)).Append('\n');
                var ls = _primary.transform.lossyScale;
                sb.Append("# rendererLossyScale ").Append(F(ls.x)).Append(' ').Append(F(ls.y)).Append(' ').Append(F(ls.z)).Append('\n');
                for (int i = 0; i < v.Length; i++) sb.Append("v ").Append(F(v[i].x)).Append(' ').Append(F(v[i].y)).Append(' ').Append(F(v[i].z)).Append('\n');
                File.WriteAllText(bakePath, sb.ToString());
                _bakeNote = Path.GetFileName(bakePath) + " (" + n + " vertices)";
                if (_primary.sharedMesh != null && _primary.sharedMesh.vertexCount != n)
                    Plugin.Log.LogWarning("Pose recorder: BakeMesh has " + n + " vertices, the shared mesh " + _primary.sharedMesh.vertexCount);
            }
            catch (Exception e)
            {
                _bakeNote = null;
                Plugin.Log.LogWarning("Pose recorder: BakeMesh skipped (" + e.GetType().Name + ": " + e.Message + ")");
            }
            finally { if (baked != null) UnityEngine.Object.Destroy(baked); }
        }

        static void Finish(string problem)
        {
            _on = false;
            try { Remove(); } catch (Exception e) { Plugin.Log.LogWarning("Pose recorder: couldn't leave the frame loop: " + e.Message); }
            try
            {
                if (_frameCount == 0) { _status = "off: nothing recorded" + (problem != null ? " (" + problem + ")" : ""); Plugin.Log.LogWarning("Pose recorder: " + _status); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, Header() + _frames);
                string msg = "Wrote " + _path + ": " + _frameCount + " frames, " + _bones.Count + " bones" + (problem != null ? " (stopped early: " + problem + ")" : "");
                Plugin.Log.LogInfo("Pose recording: " + msg);
                _status = msg;
            }
            catch (Exception e)
            {
                _status = "off: couldn't write the file: " + e.Message;
                Plugin.Log.LogError("Pose recorder: " + e);
            }
            finally { Clear(); }
        }

        static void Clear()
        {
            _c = null; _rends = null; _primary = null; _bones = null; _frames = null;
        }

        // ---- file ----

        static string Header()
        {
            var sb = new StringBuilder(4096);
            var ls = _rootScale;
            sb.Append("# SfMorph pose recording v1\n");
            sb.Append("key ").Append(_key).Append('\n');
            sb.Append("scene ").Append(_scene).Append('\n');
            sb.Append("date ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv)).Append('\n');
            sb.Append("fps ").Append(F(_fps)).Append('\n');
            sb.Append("seconds ").Append(F(_seconds)).Append('\n');
            sb.Append("rootScale ").Append(F(ls.x)).Append(' ').Append(F(ls.y)).Append(' ').Append(F(ls.z)).Append('\n');
            sb.Append(_flags).Append('\n');
            sb.Append("primary ").Append(_primary != null ? _primary.name : "?").Append('\t').Append(_primary != null && _primary.sharedMesh != null ? _primary.sharedMesh.name : "?").Append('\n');
            sb.Append("primaryToRoot ").Append(Mat16(_primaryToRoot)).Append('\n');
            sb.Append("bake ").Append(_bakeNote ?? "none").Append('\n');
            sb.Append("bones ").Append(_bones.Count).Append('\n');
            for (int i = 0; i < _bones.Count; i++) sb.Append("bone ").Append(i).Append(' ').Append(_bones[i] != null ? _bones[i].name : "-").Append('\n');
            sb.Append("frames ").Append(_frameCount).Append('\n');
            return sb.ToString();
        }

        static string Flags(Character c, Plugin p)
        {
            string apply = p != null ? Bool(p.Apply.Value) : "?";
            string soft = p != null ? Bool(p.Physics.Value) : "?";
            string embody = p != null ? Bool(p.EmbodyOn.Value) : "?";
            string hands = p != null ? Bool(p.HandsOn.Value) : "?";
            string conv = c.Look != null && c.Look.Morph != null ? c.Look.Morph : "none";
            return "flags apply=" + apply + " converted=" + conv + " softZones=" + soft + " embody=" + embody + " physicsHands=" + hands;
        }

        static string Bool(bool b) { return b ? "on" : "off"; }
        static string F(float v) { return v.ToString("0.######", Inv); }

        static string Mat16(Matrix4x4 m)
        {
            var sb = new StringBuilder();
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++) sb.Append(r + c > 0 ? " " : "").Append(F(m[r, c]));
            return sb.ToString();
        }

        // ---- what to record ----

        static SkinnedMeshRenderer Primary(List<SkinnedMeshRenderer> rends)
        {
            foreach (var r in rends)
            {
                if (r.sharedMesh == null) continue;
                var n = Orig(r.sharedMesh);
                if (n == "reika_upperBody_normal" || n == "uniform_upperBody_normal") return r;
            }
            foreach (var r in rends) if (r.sharedMesh != null) return r;
            return rends.Count > 0 ? rends[0] : null;
        }

        // The game's mesh name behind a soft-zone or morph clone.
        static string Orig(Mesh m)
        {
            var o = SoftZones.Original(m);
            var n = (o != null ? o : m).name;
            const string tag = " (SfMorph)";
            if (n.EndsWith(tag)) n = n.Substring(0, n.Length - tag.Length);
            return n;
        }

        // All distinct bones of every renderer except our soft-zone helpers: BoneList order, then the rest.
        static List<Transform> CollectBones(Character c, List<SkinnedMeshRenderer> rends)
        {
            var union = new HashSet<Transform>();
            var extra = new List<Transform>();
            foreach (var r in rends)
            {
                Transform[] bs;
                try { bs = r.bones; } catch { continue; }
                foreach (var b in bs)
                {
                    if (b == null || b.name.StartsWith(SoftZones.Prefix, StringComparison.Ordinal)) continue;
                    if (union.Add(b)) extra.Add(b);
                }
            }
            var list = new List<Transform>();
            var placed = new HashSet<Transform>();
            foreach (var b in c.BoneList)
                if (b != null && union.Contains(b) && placed.Add(b)) list.Add(b);
            foreach (var b in extra) if (placed.Add(b)) list.Add(b);
            var names = new Dictionary<string, int>();
            foreach (var b in list)
            {
                int n;
                names.TryGetValue(b.name, out n);
                names[b.name] = n + 1;
            }
            foreach (var kv in names)
                if (kv.Value > 1) Plugin.Log.LogWarning("Pose recorder: " + kv.Value + " different bones are named '" + kv.Key + "' (match them by index, not by name)");
            return list;
        }

        // ---- PlayerLoop (same pattern as ApplyStep.Ensure) ----

        static bool Insert()
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var subs = root.subSystemList;
            if (subs == null) return false;
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type != typeof(PostLateUpdate) || subs[i].subSystemList == null) continue;
                var list = new List<PlayerLoopSystem>(subs[i].subSystemList);
                if (list.Exists(s => s.type == typeof(RecordStep))) return true;
                int at = list.FindIndex(s => s.type == typeof(PostLateUpdate.UpdateAllSkinnedMeshes));
                var step = new PlayerLoopSystem { type = typeof(RecordStep), updateDelegate = Step };
                if (at < 0) list.Add(step); else list.Insert(at, step);
                subs[i].subSystemList = list.ToArray();
                root.subSystemList = subs;
                PlayerLoop.SetPlayerLoop(root);
                return true;
            }
            return false;
        }

        static void Remove()
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var subs = root.subSystemList;
            if (subs == null) return;
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type != typeof(PostLateUpdate) || subs[i].subSystemList == null) continue;
                var list = new List<PlayerLoopSystem>(subs[i].subSystemList);
                if (list.RemoveAll(s => s.type == typeof(RecordStep)) == 0) return;
                subs[i].subSystemList = list.ToArray();
                root.subSystemList = subs;
                PlayerLoop.SetPlayerLoop(root);
                return;
            }
        }
    }
}
