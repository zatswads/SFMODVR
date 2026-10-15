using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // Soft zones (stage 1 of contact deformation, no shaders): her glutes, inner/outer thighs, belly and breasts get
    // helper bones that the game's own DynamicBone simulates, so hands and limbs push the skin in instead of passing
    // through it, and the zones lag and settle a little as she moves (springiness).
    //
    // Per zone a rest point just under the skin (found on her mesh along a body direction). For every bone that
    // weights the skin there, a helper pair is added under that bone: anchor A (DynamicBone root, never moves) and
    // soft S (the simulated particle, a pendulum hanging from A along the skin, so it can swing in and out). Skin
    // weights are split, not moved: a vertex's weight w on bone b becomes w(1-f) on b and w f on S_b (f = smooth
    // falloff from the zone's centre), so at rest and in every pose without contact she deforms exactly as before;
    // only S's displacement (collision, inertia) is added. A gets a second, excluded child so DynamicBone never
    // rotates it (it re-aims a bone only when it has one child); S therefore only translates.
    //
    // The meshes: her renderers show readable clones carrying the extra bones. With a converted character on, its
    // clones (MorphState) are extended in place; without one, the game's GPU-only meshes get our own clones (built
    // from the GPU readback) and MorphState treats them as the game's (Original()). Rebuilt when the game or the
    // morph swaps a mesh; everything (bones, meshes, weights, bind poses, the game's DynamicBone lists) is given back
    // on release. Helper bones are ours only: Body's trackers never write them.
    internal class SoftZones
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public const string Prefix = "SfSoft";

        // ---- the zones (her AdvancedSkeleton rig) ----

        enum Toward { Back, Forward, Out, In }

        class Zone
        {
            public string Id, Label;
            public string From, To; public float At;   // ray origin: lerp(From, To, At) in bind space
            public Toward Dir;
            public int Side;                            // +1 her left, -1 her right, 0 middle
            public string[] Region;                     // bones (with "*": and everything under it) the skin must belong to
            public float Radius, Ball, Lean;            // falloff radius, particle radius (depth under the skin), pendulum length (m)
            public bool InnerThigh;
        }

        static Zone Z(string id, string label, string from, string to, float at, Toward dir, int side, float radius, float ball, float lean, params string[] region)
        {
            return new Zone { Id = id, Label = label, From = from, To = to, At = at, Dir = dir, Side = side, Region = region, Radius = radius, Ball = ball, Lean = lean };
        }

        static readonly Zone[] Zones = Build();

        static Zone[] Build()
        {
            var l = new List<Zone>();
            foreach (var s in new[] { 1, -1 })
            {
                string x = s > 0 ? "_L" : "_R", side = s > 0 ? "Left_" : "Right_";
                l.Add(Z("glute" + x, "glute", "Butt" + x, "Butt" + x, 0f, Toward.Back, s, 0.09f, 0.035f, 0.045f, "Butt" + x + "*", "Hip" + x, "HipPart1" + x, "Root_M", "Ass_Outer"));
                l.Add(Z("thighOut" + x, "outer thigh", "Hip" + x, "Knee" + x, 0.35f, Toward.Out, s, 0.08f, 0.03f, 0.04f, "Hip" + x + "*", "Butt" + x + "*"));
                var inner = Z("thighIn" + x, "inner thigh", "Hip" + x, "Knee" + x, 0.3f, Toward.In, s, 0.07f, 0.03f, 0.04f, "Hip" + x + "*");
                inner.InnerThigh = true;
                l.Add(inner);
                l.Add(Z("breastFront" + x, "breast front", side + "LowerBreast", side + "LowerBreast", 0f, Toward.Forward, s, 0.05f, 0.025f, 0.03f, side + "UpperBreast*"));
                l.Add(Z("breastSide" + x, "breast side", side + "LowerBreast", side + "LowerBreast", 0f, Toward.Out, s, 0.05f, 0.025f, 0.03f, side + "UpperBreast*"));
            }
            l.Add(Z("belly", "belly", "Root_M", "Chest_M", 0.35f, Toward.Forward, 0, 0.09f, 0.035f, 0.045f, "Root_M", "Spine1_M", "Vaginal_root*"));
            return l.ToArray();
        }

        // ---- game types (reflection: Assembly-CSharp isn't referenced) ----

        static bool _typesLooked, _surveyed;
        static Type _db, _dbc;
        static FieldInfo _fRoot, _fColliders, _fExclusions;
        static MethodInfo _mUpdateParams, _mSetup;

        public static bool Available { get { Types(); return _db != null && _dbc != null; } }

        static void Types()
        {
            if (_typesLooked) return;
            _typesLooked = true;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = a.GetName().Name;
                if (n != "Assembly-CSharp" && n != "Assembly-CSharp-firstpass") continue;
                _db = _db ?? a.GetType("DynamicBone");
                _dbc = _dbc ?? a.GetType("DynamicBoneCollider");
            }
            if (_db == null) return;
            _fRoot = _db.GetField("m_Root", Any);
            _fColliders = _db.GetField("m_Colliders", Any);
            _fExclusions = _db.GetField("m_Exclusions", Any);
            _mUpdateParams = _db.GetMethod("UpdateParameters", Any, null, Type.EmptyTypes, null);
            _mSetup = _db.GetMethod("SetupParticles", Any, null, Type.EmptyTypes, null);
            if (_fRoot == null || _fColliders == null) _db = null;
        }

        static void Set(object o, string f, object v)
        {
            var fi = o.GetType().GetField(f, Any);
            if (fi == null) return;
            if (fi.FieldType.IsEnum && !(v is Enum)) v = Enum.ToObject(fi.FieldType, v);
            fi.SetValue(o, v);
        }

        static object Get(object o, string f)
        {
            if (o == null) return null;
            var fi = o.GetType().GetField(f, Any);
            return fi != null ? fi.GetValue(o) : null;
        }

        static IList ListField(Component db, FieldInfo f)
        {
            var l = f.GetValue(db) as IList;
            if (l == null) { l = (IList)Activator.CreateInstance(f.FieldType); f.SetValue(db, l); }
            return l;
        }

        // One log block per session: what the game's DynamicBone looks like and how its own physics is set up.
        static void Survey(Transform her)
        {
            if (_surveyed) return;
            _surveyed = true;
            var sb = new StringBuilder("Physics survey: ");
            foreach (var t in new[] { _db, _dbc, _dbc != null ? _dbc.BaseType : null })
            {
                if (t == null) continue;
                sb.Append("\n  ").Append(t.Name).Append(" fields:");
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)) sb.Append(' ').Append(f.Name).Append(':').Append(f.FieldType.Name);
            }
            int n = 0;
            foreach (var mb in UnityEngine.Object.FindObjectsOfType(_db))
            {
                var db = mb as Component;
                if (db == null || db.name.StartsWith(Prefix)) continue;
                if (++n > 40) continue;
                var root = _fRoot.GetValue(db) as Transform;
                var cols = _fColliders.GetValue(db) as IList;
                sb.Append("\n  DynamicBone on ").Append(db.name).Append(" root=").Append(root != null ? root.name : "-")
                  .Append(her != null && root != null && root.IsChildOf(her) ? " (hers)" : "")
                  .Append(" enabled=").Append(((Behaviour)db).enabled)
                  .Append(" rate=").Append(Get(db, "m_UpdateRate")).Append(" damp=").Append(Get(db, "m_Damping")).Append(" elast=").Append(Get(db, "m_Elasticity"))
                  .Append(" stiff=").Append(Get(db, "m_Stiffness")).Append(" inert=").Append(Get(db, "m_Inert")).Append(" radius=").Append(Get(db, "m_Radius"))
                  .Append(" colliders=").Append(cols != null ? cols.Count : 0);
                if (cols != null) foreach (var c in cols) { var cc = c as Component; if (cc != null) sb.Append(' ').Append(cc.name); }
            }
            if (n > 40) sb.Append("\n  ... ").Append(n - 40).Append(" more DynamicBones");
            int m = 0;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(_dbc))
            {
                var c = o as Component;
                if (c == null || c.name.StartsWith(Prefix)) continue;
                if (++m > 40) continue;
                sb.Append("\n  collider ").Append(c.name).Append(" (parent ").Append(c.transform.parent != null ? c.transform.parent.name : "-").Append(") r=")
                  .Append(Get(c, "m_Radius")).Append(" h=").Append(Get(c, "m_Height")).Append(" dir=").Append(Get(c, "m_Direction")).Append(" bound=").Append(Get(c, "m_Bound"))
                  .Append(" scale=").Append(c.transform.lossyScale.x.ToString("0.###"));
            }
            sb.Append("\n  ").Append(n).Append(" DynamicBones, ").Append(m).Append(" DynamicBoneColliders in the scene");
            Plugin.Log.LogInfo(sb.ToString());
        }

        // ---- state ----

        class Rs   // one of her renderers
        {
            public Mesh Base;          // what it showed before us: the game's mesh or MorphState's clone
            public Mesh Mesh;          // what it shows with us (our clone, or Base extended in place; Base when not used)
            public Transform[] Bones;  // its bones without ours
            public bool Used, InPlace;
            public BoneWeight[] Saved; // in place: the clone's own weights
            public int BaseCount;      // bind poses before ours
            public Matrix4x4 ToRef;    // this mesh's bind space -> the reference mesh's
        }

        class Helper
        {
            public Zone Zone;
            public Transform Bone, A, S;
            public Component Db;
            public Vector3 SLocal;       // S in Bone's space (A has no rotation or scale of its own)
            public Matrix4x4 BindRef;    // S's bind pose in the reference mesh's space
            public float Total;          // how much skin it carries (vertex weight sum)
        }

        class Col { public Component C; public Transform Owner; public string Kind; }   // Kind: partner, hand, forearm, thighL, thighR

        readonly Dictionary<SkinnedMeshRenderer, Rs> _rs = new Dictionary<SkinnedMeshRenderer, Rs>();
        readonly List<Helper> _helpers = new List<Helper>();
        readonly HashSet<Component> _ours = new HashSet<Component>();
        readonly List<Col> _cols = new List<Col>();
        readonly HashSet<Transform> _partnerRoots = new HashSet<Transform>();
        readonly List<KeyValuePair<IList, object>> _gameAdds = new List<KeyValuePair<IList, object>>();  // items we put into the game's DynamicBone lists
        readonly Dictionary<Mesh, MeshData> _data = new Dictionary<Mesh, MeshData>();
        static readonly Dictionary<Mesh, Mesh> _origOf = new Dictionary<Mesh, Mesh>();          // our clone -> the game's mesh
        List<SkinnedMeshRenderer> _builtFor;
        Transform _her;
        float _builtSoft = -1f, _softChangedAt, _lastBuild = -10f, _nextCheck, _builtMorph;
        float _appliedSpring = -1f, _appliedSoft = -1f;
        int _bindVersion;
        bool _herCols;
        public string Status = "";

        // The game's mesh behind one of our clones (MorphState must clone the game's mesh, not ours).
        public static Mesh Original(Mesh m)
        {
            Mesh o;
            return m != null && _origOf.TryGetValue(m, out o) && o != null ? o : m;
        }

        static bool IsMorphClone(Mesh m) { return m != null && m.isReadable && m.name.EndsWith(" (SfMorph)"); }

        // Every frame, after MorphState.Update and before Body applies: on = Apply + Physics on + the game's woman.
        public void Update(Character c, bool on, float softness, float spring, List<Character> everyone)
        {
            if (!on || c.Root == null) { Release(); return; }
            if (!Available) { Status = "off: the game has no DynamicBone class"; return; }
            softness = Mathf.Clamp01(softness); spring = Mathf.Clamp01(spring);

            string why = null;
            if (_builtFor == null) why = "physics on";
            else if (_her != c.Root || !SameList(_builtFor, c.Renderers)) why = "her renderers changed";
            else
                foreach (var kv in _rs)
                    if (kv.Key == null || kv.Key.sharedMesh != kv.Value.Mesh)
                    { why = (kv.Key != null ? kv.Key.name : "a renderer") + "'s mesh was swapped"; break; }
            if (why == null && Mathf.Abs(softness - _builtSoft) > 0.001f)
            {
                if (_softChangedAt < 0f) _softChangedAt = Time.unscaledTime;
                if (Time.unscaledTime - _softChangedAt > 0.4f) why = "softness changed";
            }
            else if (Mathf.Abs(softness - _builtSoft) <= 0.001f) _softChangedAt = -1f;
            if (why == null && Time.unscaledTime >= _nextCheck)
            {
                _nextCheck = Time.unscaledTime + 2f;
                if (Mathf.Abs(MorphWeight() - _builtMorph) > 0.1f) why = "the character shape's weight changed";
                else Colliders(c, everyone, false);
            }
            if (why != null)
            {
                Teardown(false);   // consistent right away (never their bones with a mesh that lacks our bind poses)
                if (Time.unscaledTime - _lastBuild < 0.5f) return;  // no rebuild storms; try again next frame
                _lastBuild = Time.unscaledTime;
                Build(c, softness, everyone, why);
                _appliedSpring = -1f;
            }
            if (MorphState.BindVersion != _bindVersion) { _bindVersion = MorphState.BindVersion; RefreshBind(); }
            if (spring != _appliedSpring || softness != _appliedSoft)
            {
                foreach (var h in _helpers) if (h.Db != null) Params(h.Db, softness, spring);
                _appliedSpring = spring; _appliedSoft = softness;
            }
        }

        float MorphWeight()
        {
            foreach (var kv in _rs)
                if (kv.Key != null && kv.Value.Used && kv.Value.Base != null && kv.Value.Base.blendShapeCount > 0) return kv.Key.GetBlendShapeWeight(0) / 100f;
            return 0f;
        }

        static bool SameList(List<SkinnedMeshRenderer> a, List<SkinnedMeshRenderer> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ---- building ----

        class Src   // a renderer's mesh as the zones see it (reference bind space)
        {
            public SkinnedMeshRenderer R; public Rs S;
            public Vector3[] P, N; public BoneWeight[] W; public Matrix4x4[] Bind;
            public bool Skin;
            public Dictionary<int, float[]> F = new Dictionary<int, float[]>();  // vertex -> per-zone fraction
        }

        void Build(Character c, float softness, List<Character> everyone, string why)
        {
            var t0 = Time.realtimeSinceStartup;
            _her = c.Root;
            _builtFor = new List<SkinnedMeshRenderer>(c.Renderers);
            _builtSoft = softness; _softChangedAt = -1f;
            Survey(c.Root);
            var problems = new List<string>();

            // 1. her meshes: positions (with the character shape at its current weight), normals, weights, bind poses
            var srcs = new List<Src>();
            foreach (var r in c.Renderers)
            {
                if (r == null || r.sharedMesh == null) continue;
                var rs = new Rs { Base = r.sharedMesh, Mesh = r.sharedMesh };
                _rs[r] = rs;
                var bones = r.bones;
                var keep = new List<Transform>(bones.Length);
                foreach (var b in bones) if (b == null || !b.name.StartsWith(Prefix)) keep.Add(b);
                rs.Bones = keep.ToArray();
                if (keep.Count != bones.Length) r.bones = rs.Bones;
                bool relevant = false;   // only meshes weighted to a zone's body part (not hair, face, lashes)
                foreach (var reg in Regions(c, rs.Bones)) { foreach (var x in reg) if (x) { relevant = true; break; } if (relevant) break; }
                if (!relevant) continue;
                var src = Read(r, rs, problems);
                if (src != null) srcs.Add(src);
            }
            if (srcs.Count == 0) { Status = "off: none of her meshes could be read" + (problems.Count > 0 ? " (" + problems[0] + ")" : ""); Log(c, why, t0, problems); return; }

            // 2. one reference bind space: the biggest body mesh; the others mapped through a shared bone
            Src refSrc = null;
            foreach (var s in srcs) if (s.Skin && (refSrc == null || s.P.Length > refSrc.P.Length)) refSrc = s;
            if (refSrc == null) foreach (var s in srcs) if (refSrc == null || s.P.Length > refSrc.P.Length) refSrc = s;
            var bindRef = new Dictionary<Transform, Matrix4x4>();
            var rb = refSrc.S.Bones;
            for (int i = 0; i < rb.Length && i < refSrc.Bind.Length; i++) if (rb[i] != null) bindRef[rb[i]] = refSrc.Bind[i];
            var mapped = new List<Src>();
            foreach (var s in srcs)
            {
                if (s == refSrc) { s.S.ToRef = Matrix4x4.identity; mapped.Add(s); continue; }
                var sum = new float[s.S.Bones.Length];
                foreach (var w in s.W) { if (w.boneIndex0 < sum.Length) sum[w.boneIndex0] += w.weight0; if (w.boneIndex1 < sum.Length) sum[w.boneIndex1] += w.weight1; }
                int best = -1;
                for (int i = 0; i < s.S.Bones.Length && i < s.Bind.Length; i++)
                    if (s.S.Bones[i] != null && bindRef.ContainsKey(s.S.Bones[i]) && (best < 0 || sum[i] > sum[best])) best = i;
                if (best < 0) continue;   // shares no bone with the body: nothing of the zones can reach it
                s.S.ToRef = bindRef[s.S.Bones[best]].inverse * s.Bind[best];
                mapped.Add(s);
            }
            foreach (var s in mapped)   // bones only other meshes have
            {
                var inv = s.S.ToRef.inverse;
                for (int i = 0; i < s.S.Bones.Length && i < s.Bind.Length; i++)
                    if (s.S.Bones[i] != null && !bindRef.ContainsKey(s.S.Bones[i])) bindRef[s.S.Bones[i]] = s.Bind[i] * inv;
            }
            foreach (var s in mapped)
                if (s != refSrc)
                {
                    var m = s.S.ToRef;
                    for (int i = 0; i < s.P.Length; i++) { s.P[i] = m.MultiplyPoint3x4(s.P[i]); if (s.N != null) s.N[i] = m.MultiplyVector(s.N[i]).normalized; }
                }

            // 3. body directions and units in the reference space
            Func<string, Vector3?> pos = name =>
            {
                Transform t; Matrix4x4 m;
                return c.Bones.TryGetValue(name, out t) && t != null && bindRef.TryGetValue(t, out m) ? (Vector3?)m.inverse.GetColumn(3) : null;
            };
            Vector3? hl = pos("Hip_L"), hr = pos("Hip_R"), pr = pos("Root_M"), ch = pos("Chest_M"), kl = pos("Knee_L");
            if (!hl.HasValue || !hr.HasValue || !pr.HasValue || !ch.HasValue)
            {
                Status = "off: her rig has no Hip_L/Hip_R/Root_M/Chest_M (not the game's woman?)";
                Log(c, why, t0, problems);
                return;
            }
            var left = (hl.Value - hr.Value).normalized;
            var up = (ch.Value - pr.Value).normalized;
            var fwd = Vector3.Cross(up, left).normalized;
            up = Vector3.Cross(left, fwd).normalized;
            Vector3? toe = pos("Toes_L"), ankle = pos("Ankle_L");
            if (toe.HasValue && ankle.HasValue && Vector3.Dot(toe.Value - ankle.Value, fwd) < 0f) fwd = -fwd;
            var center = pr.Value;
            float mpu = 1f;   // metres per reference unit
            Transform thl, tkl;
            if (kl.HasValue && c.Bones.TryGetValue("Hip_L", out thl) && c.Bones.TryGetValue("Knee_L", out tkl) && (kl.Value - hl.Value).magnitude > 1e-6f)
                mpu = (thl.position - tkl.position).magnitude / (kl.Value - hl.Value).magnitude;
            if (mpu <= 1e-6f || float.IsNaN(mpu)) mpu = 1f;

            // 4. zone rest points on the skin, then each vertex's fraction per zone
            var zoneQ = new Vector3?[Zones.Length]; var zoneN = new Vector3[Zones.Length];
            var regionOf = new Dictionary<Src, bool[][]>();
            foreach (var s in mapped) regionOf[s] = Regions(c, s.S.Bones);
            var found = new List<string>(); var missing = new List<string>();
            float fmax = Mathf.Lerp(0.35f, 1f, softness);
            for (int z = 0; z < Zones.Length; z++)
            {
                var zone = Zones[z];
                var a = pos(zone.From); var b = pos(zone.To);
                if (!a.HasValue || !b.HasValue) { missing.Add(zone.Id + " (no " + zone.From + "/" + zone.To + ")"); continue; }
                var o = Vector3.Lerp(a.Value, b.Value, zone.At);
                var d = zone.Dir == Toward.Back ? -fwd : zone.Dir == Toward.Forward ? fwd : zone.Dir == Toward.Out ? left * (zone.Side != 0 ? zone.Side : 1) : -left * (zone.Side != 0 ? zone.Side : 1);
                Vector3 q = Vector3.zero, qn = d; bool hit = false;
                foreach (var pass in new[] { 0, 1, 2 })   // body meshes, narrow ray; wider; any mesh
                {
                    float lateral = (pass == 0 ? 0.03f : 0.06f) / mpu, bestT = 0f;
                    foreach (var s in mapped)
                    {
                        if (pass < 2 && !s.Skin) continue;
                        var reg = regionOf[s][z];
                        for (int i = 0; i < s.P.Length; i++)
                        {
                            if (RegionWeight(s.W[i], reg) < 0.4f) continue;
                            var p = s.P[i];
                            if (zone.Side != 0 && Vector3.Dot(p - center, left) * zone.Side * mpu < 0.005f) continue;
                            var v = p - o; float t = Vector3.Dot(v, d);
                            if (t <= bestT || (v - d * t).magnitude > lateral) continue;
                            bestT = t; q = p; qn = s.N != null ? s.N[i] : d; hit = true;
                        }
                    }
                    if (hit) break;
                }
                if (!hit) { missing.Add(zone.Id + " (no skin found)"); continue; }
                zoneQ[z] = q; zoneN[z] = (qn + d).normalized;
                found.Add(zone.Id);
            }
            foreach (var s in mapped)
            {
                var reg = regionOf[s];
                for (int i = 0; i < s.P.Length; i++)
                {
                    float[] fz = null; float total = 0f;
                    for (int z = 0; z < Zones.Length; z++)
                    {
                        if (!zoneQ[z].HasValue) continue;
                        var zone = Zones[z];
                        float dist = (s.P[i] - zoneQ[z].Value).magnitude * mpu;
                        if (dist >= zone.Radius) continue;
                        float g = Mathf.Clamp01((RegionWeight(s.W[i], reg[z]) - 0.15f) / 0.35f);
                        if (zone.Side != 0) g *= Mathf.Clamp01(Vector3.Dot(s.P[i] - center, left) * zone.Side * mpu / 0.015f);
                        if (s.N != null) g *= Mathf.Clamp01(Vector3.Dot(s.N[i], zoneN[z]) * 1.5f + 0.5f);
                        float t = 1f - dist / zone.Radius;
                        float f = fmax * t * t * (3f - 2f * t) * g;
                        if (f < 0.005f) continue;
                        if (fz == null) fz = new float[Zones.Length];
                        fz[z] = f; total += f;
                    }
                    if (fz == null) continue;
                    if (total > fmax) for (int z = 0; z < fz.Length; z++) fz[z] *= fmax / total;
                    s.F[i] = fz;
                }
            }

            // 5. helpers: per zone, the bones that carry its skin (a bone the game's physics chain re-aims is left alone)
            var gameDbs = GameDbs(c.Root);
            var noSplit = new HashSet<Transform>();
            // DynamicBone re-aims a chain bone only while it has at most one child: a helper under such a bone would
            // freeze its orientation, so only chain bones that already have several children get helpers.
            foreach (var g in gameDbs) foreach (var t in g.Value) if (t.childCount < 2) noSplit.Add(t);
            var carry = new Dictionary<long, float>();   // zone * 4096 + bone index in bindRef order
            var boneIds = new Dictionary<Transform, int>(); var boneList = new List<Transform>();
            foreach (var s in mapped)
                foreach (var kv in s.F)
                {
                    var w = s.W[kv.Key];
                    for (int k = 0; k < 4; k++)
                    {
                        int bi = k == 0 ? w.boneIndex0 : k == 1 ? w.boneIndex1 : k == 2 ? w.boneIndex2 : w.boneIndex3;
                        float bw = k == 0 ? w.weight0 : k == 1 ? w.weight1 : k == 2 ? w.weight2 : w.weight3;
                        if (bw <= 0f || bi >= s.S.Bones.Length || s.S.Bones[bi] == null) continue;
                        var bone = s.S.Bones[bi];
                        int id;
                        if (!boneIds.TryGetValue(bone, out id)) { id = boneList.Count; boneIds[bone] = id; boneList.Add(bone); }
                        for (int z = 0; z < Zones.Length; z++)
                            if (kv.Value[z] > 0f) { long key = (long)z * 4096 + id; float cur; carry.TryGetValue(key, out cur); carry[key] = cur + bw * kv.Value[z]; }
                    }
                }
            var helperOf = new Dictionary<long, int>();
            int skippedChain = 0;
            for (int z = 0; z < Zones.Length; z++)
            {
                if (!zoneQ[z].HasValue) continue;
                var cands = new List<KeyValuePair<int, float>>();
                foreach (var kv in carry) if (kv.Key / 4096 == z && kv.Value >= 1f) cands.Add(new KeyValuePair<int, float>((int)(kv.Key % 4096), kv.Value));
                cands.Sort((x, y) => y.Value.CompareTo(x.Value));
                int made = 0;
                foreach (var cand in cands)
                {
                    if (made >= 4) break;
                    var bone = boneList[cand.Key];
                    Matrix4x4 bp;
                    if (!bindRef.TryGetValue(bone, out bp)) continue;
                    if (noSplit.Contains(bone)) { skippedChain++; continue; }
                    var h = MakeHelper(Zones[z], bone, bp, zoneQ[z].Value, zoneN[z], up, fwd, mpu, gameDbs);
                    if (h == null) continue;
                    h.Total = cand.Value;
                    helperOf[(long)z * 4096 + cand.Key] = _helpers.Count;
                    _helpers.Add(h);
                    made++;
                }
            }

            // 6. the meshes: split weights onto the helpers, bind poses for them, swap in
            int used = 0;
            var meshNames = new List<string>();
            var hb = new Transform[_helpers.Count];
            for (int i = 0; i < hb.Length; i++) hb[i] = _helpers[i].S;
            foreach (var s in mapped)
            {
                if (s.F.Count == 0 || _helpers.Count == 0) continue;
                var nw = (BoneWeight[])s.W.Clone();
                int changed = 0;
                var list = new List<KeyValuePair<int, float>>(12);
                foreach (var kv in s.F)
                {
                    var w = s.W[kv.Key]; list.Clear();
                    bool any = false;
                    for (int k = 0; k < 4; k++)
                    {
                        int bi = k == 0 ? w.boneIndex0 : k == 1 ? w.boneIndex1 : k == 2 ? w.boneIndex2 : w.boneIndex3;
                        float bw = k == 0 ? w.weight0 : k == 1 ? w.weight1 : k == 2 ? w.weight2 : w.weight3;
                        if (bw <= 0f) continue;
                        float stay = bw;
                        int id;
                        if (bi < s.S.Bones.Length && s.S.Bones[bi] != null && boneIds.TryGetValue(s.S.Bones[bi], out id))
                            for (int z = 0; z < Zones.Length; z++)
                            {
                                int h;
                                if (kv.Value[z] <= 0f || !helperOf.TryGetValue((long)z * 4096 + id, out h)) continue;
                                Add(list, s.S.Bones.Length + h, bw * kv.Value[z]);
                                stay -= bw * kv.Value[z];
                                any = true;
                            }
                        Add(list, bi, stay);
                    }
                    if (!any) continue;
                    list.Sort((x, y) => y.Value.CompareTo(x.Value));
                    float sum = 0f;
                    for (int k = 0; k < 4 && k < list.Count; k++) sum += list[k].Value;
                    if (sum <= 1e-6f) continue;
                    var o = new BoneWeight();
                    for (int k = 0; k < 4 && k < list.Count; k++)
                    {
                        float v = list[k].Value / sum; int bi = list[k].Key;
                        if (k == 0) { o.boneIndex0 = bi; o.weight0 = v; } else if (k == 1) { o.boneIndex1 = bi; o.weight1 = v; }
                        else if (k == 2) { o.boneIndex2 = bi; o.weight2 = v; } else { o.boneIndex3 = bi; o.weight3 = v; }
                    }
                    nw[kv.Key] = o;
                    changed++;
                }
                if (changed == 0) continue;
                Augment(s, nw, hb, problems);
                if (s.S.Used) { used++; meshNames.Add(s.R.name + "(" + changed + ")"); }
            }

            Colliders(c, everyone, true);
            AssignColliders();
            _builtMorph = MorphWeight();
            _bindVersion = MorphState.BindVersion;
            int partner = 0, own = 0;
            foreach (var col in _cols) if (col.Kind == "partner") partner++; else own++;
            Status = _helpers.Count > 0
                ? "on: " + found.Count + " zones, " + _helpers.Count + " helper bones on " + used + " meshes; colliders: " + partner + " on partner hands, " + own + " on her arms/thighs"
                  + (partner == 0 ? " (no partner hands found yet)" : "")
                : "off: no zone found skin to soften (see the log)";
            var sb = new StringBuilder();
            sb.Append("Soft zones on '").Append(c.Key).Append("' (").Append(why).Append("): ").Append(found.Count).Append(" zones, ").Append(_helpers.Count).Append(" helper bones, ")
              .Append(used).Append(" meshes [").Append(string.Join(", ", meshNames.ToArray())).Append("], ").Append(partner).Append(" partner colliders, ").Append(own)
              .Append(" of hers; softness ").Append(softness.ToString("0.00")).Append(", ").Append(mpu.ToString("0.####")).Append(" m/unit, morph ").Append(_builtMorph.ToString("0.00"))
              .Append(", built in ").Append(((Time.realtimeSinceStartup - t0) * 1000f).ToString("0")).Append(" ms");
            if (skippedChain > 0) sb.Append("; ").Append(skippedChain).Append(" bones left alone (the game's physics re-aims them)");
            if (missing.Count > 0) sb.Append("\n  zones not placed: ").Append(string.Join(", ", missing.ToArray()));
            sb.Append("\n  helpers:");
            foreach (var h in _helpers) sb.Append(' ').Append(h.Zone.Id).Append('@').Append(h.Bone.name).Append('(').Append(h.Total.ToString("0")).Append(')');
            Plugin.Log.LogInfo(sb.ToString());
            Log(c, null, t0, problems);
        }

        static void Add(List<KeyValuePair<int, float>> l, int bone, float w)
        {
            if (w <= 1e-6f) return;
            for (int i = 0; i < l.Count; i++) if (l[i].Key == bone) { l[i] = new KeyValuePair<int, float>(bone, l[i].Value + w); return; }
            l.Add(new KeyValuePair<int, float>(bone, w));
        }

        void Log(Character c, string why, float t0, List<string> problems)
        {
            if (why != null) Plugin.Log.LogInfo("Soft zones on '" + c.Key + "' (" + why + "): " + Status);
            if (problems.Count > 0) Plugin.Log.LogWarning("Soft zones: " + string.Join("; ", problems.ToArray()));
        }

        // A renderer's current mesh as data: MorphState's readable clone (its shape at the current weight) or the game's
        // GPU-only mesh read back.
        Src Read(SkinnedMeshRenderer r, Rs rs, List<string> problems)
        {
            var m = r.sharedMesh;
            var s = new Src { R = r, S = rs, Skin = m.name.IndexOf("body", StringComparison.OrdinalIgnoreCase) >= 0 };
            if (IsMorphClone(m))
            {
                rs.InPlace = true;
                s.P = m.vertices; s.N = m.normals; s.W = m.boneWeights; s.Bind = m.bindposes;
                if (s.N != null && s.N.Length != s.P.Length) s.N = null;
                if (m.blendShapeCount > 0)
                {
                    float w = r.GetBlendShapeWeight(0) / 100f;
                    if (w > 0f)
                    {
                        var dv = new Vector3[s.P.Length]; var dn = new Vector3[s.P.Length];
                        m.GetBlendShapeFrameVertices(0, m.GetBlendShapeFrameCount(0) - 1, dv, dn, null);
                        for (int i = 0; i < s.P.Length; i++) { s.P[i] += dv[i] * w; if (s.N != null) s.N[i] = (s.N[i] + dn[i] * w).normalized; }
                    }
                }
            }
            else
            {
                MeshData d;
                if (!_data.TryGetValue(m, out d))
                {
                    string err;
                    d = MeshData.Read(m, out err);
                    if (d == null) { problems.Add(m.name + ": " + err); return null; }
                    _data[m] = d;
                }
                s.P = (Vector3[])d.Pos.Clone(); s.N = d.Nrm != null && d.Nrm.Length == d.Pos.Length ? (Vector3[])d.Nrm.Clone() : null;
                s.W = d.Weights; s.Bind = d.Bind;
            }
            if (s.W == null || s.W.Length != s.P.Length || s.Bind == null || s.Bind.Length == 0) return null;   // not skinned
            rs.BaseCount = s.Bind.Length;
            return s;
        }

        // Per zone, which of this mesh's bones count as the zone's body part.
        static bool[][] Regions(Character c, Transform[] bones)
        {
            var r = new bool[Zones.Length][];
            for (int z = 0; z < Zones.Length; z++)
            {
                r[z] = new bool[bones.Length];
                var roots = new List<KeyValuePair<Transform, bool>>();
                foreach (var n in Zones[z].Region)
                {
                    bool sub = n.EndsWith("*");
                    Transform t;
                    if (c.Bones.TryGetValue(sub ? n.Substring(0, n.Length - 1) : n, out t) && t != null) roots.Add(new KeyValuePair<Transform, bool>(t, sub));
                }
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null) continue;
                    foreach (var kv in roots) if (bones[i] == kv.Key || (kv.Value && bones[i].IsChildOf(kv.Key))) { r[z][i] = true; break; }
                }
            }
            return r;
        }

        static float RegionWeight(BoneWeight w, bool[] reg)
        {
            float s = 0f;
            if (w.boneIndex0 < reg.Length && reg[w.boneIndex0]) s += w.weight0;
            if (w.boneIndex1 < reg.Length && reg[w.boneIndex1]) s += w.weight1;
            if (w.boneIndex2 < reg.Length && reg[w.boneIndex2]) s += w.weight2;
            if (w.boneIndex3 < reg.Length && reg[w.boneIndex3]) s += w.weight3;
            return s;
        }

        // The game's DynamicBones on her, with the bones each simulates (their root and everything under it).
        Dictionary<Component, List<Transform>> GameDbs(Transform her)
        {
            var d = new Dictionary<Component, List<Transform>>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(_db))
            {
                var db = o as Component;
                if (db == null || _ours.Contains(db)) continue;
                var root = _fRoot.GetValue(db) as Transform;
                if (root == null || !root.IsChildOf(her)) continue;
                d[db] = new List<Transform>(root.GetComponentsInChildren<Transform>(true));
            }
            return d;
        }

        Helper MakeHelper(Zone z, Transform bone, Matrix4x4 bp, Vector3 q, Vector3 n, Vector3 up, Vector3 fwd, float mpu, Dictionary<Component, List<Transform>> gameDbs)
        {
            // rest point a particle radius under the skin; the anchor above it along the skin (a pendulum that swings in/out)
            var s = q - n * (z.Ball / mpu);
            var tangent = up - n * Vector3.Dot(up, n);
            if (tangent.sqrMagnitude < 1e-8f) tangent = fwd - n * Vector3.Dot(fwd, n);
            var a = s + tangent.normalized * (z.Lean / mpu);
            var sLocal = bp.MultiplyPoint3x4(s); var aLocal = bp.MultiplyPoint3x4(a);
            var ga = new GameObject(Prefix + "_" + z.Id + "_" + bone.name);
            ga.transform.SetParent(bone, false);
            ga.transform.localPosition = aLocal; ga.transform.localRotation = Quaternion.identity; ga.transform.localScale = Vector3.one;
            var gs = new GameObject(Prefix + "_" + z.Id + "_" + bone.name + "_soft");
            gs.transform.SetParent(ga.transform, false);
            gs.transform.localPosition = sLocal - aLocal;
            var pin = new GameObject(Prefix + "_pin");   // second child: DynamicBone then never re-aims the anchor
            pin.transform.SetParent(ga.transform, false);
            var db = ga.AddComponent(_db);
            _ours.Add(db);
            _fRoot.SetValue(db, ga.transform);
            if (_fExclusions != null) ListField(db, _fExclusions).Add(pin.transform);
            float scale = Mathf.Abs(ga.transform.lossyScale.x); if (scale < 1e-6f) scale = 1f;
            Set(db, "m_Radius", z.Ball / scale);
            Set(db, "m_EndLength", 0f); Set(db, "m_EndOffset", Vector3.zero);
            Set(db, "m_Gravity", Vector3.zero); Set(db, "m_Force", Vector3.zero);
            Set(db, "m_DistantDisable", false);
            Set(db, "m_Friction", 0.4f);
            Set(db, "m_UpdateRate", GameRate(gameDbs));
            if (_mSetup != null) try { _mSetup.Invoke(db, null); } catch (Exception e) { Plugin.Log.LogWarning("Soft zones: SetupParticles: " + e.Message); }
            // the game's chains that hold this bone must not pick our helpers up if they ever rebuild
            if (_fExclusions != null)
                foreach (var g in gameDbs)
                    if (g.Value.Contains(bone)) { var l = ListField(g.Key, _fExclusions); l.Add(ga.transform); _gameAdds.Add(new KeyValuePair<IList, object>(l, ga.transform)); }
            return new Helper { Zone = z, Bone = bone, A = ga.transform, S = gs.transform, Db = db, SLocal = sLocal, BindRef = Matrix4x4.Translate(-sLocal) * bp };
        }

        static float GameRate(Dictionary<Component, List<Transform>> gameDbs)
        {
            foreach (var g in gameDbs) { var v = Get(g.Key, "m_UpdateRate"); if (v is float && (float)v > 0f) return (float)v; }
            return 90f;
        }

        // Softness: how much the skin gives (stiffness, and the share of skin the helpers carry, set when built).
        // Springiness: secondary motion from her own animation (lag from inertia, slower settling); 0 = none.
        static void Params(Component db, float soft, float spring)
        {
            Set(db, "m_Stiffness", Mathf.Lerp(0.6f, 0.1f, soft));
            Set(db, "m_Elasticity", Mathf.Lerp(0.22f, 0.05f, spring) * Mathf.Lerp(1.3f, 0.8f, soft));
            Set(db, "m_Damping", Mathf.Lerp(0.5f, 0.1f, spring));
            Set(db, "m_Inert", Mathf.Lerp(0.95f, 0.1f, spring));
            if (_mUpdateParams != null) try { _mUpdateParams.Invoke(db, null); } catch { }
        }

        void Augment(Src s, BoneWeight[] nw, Transform[] hb, List<string> problems)
        {
            var rs = s.S; var r = s.R;
            int baseN = rs.BaseCount;
            var bind = new Matrix4x4[baseN + hb.Length];
            Array.Copy(s.Bind, bind, baseN);
            HelperBind(rs, bind);
            var bones = new Transform[rs.Bones.Length + hb.Length];
            Array.Copy(rs.Bones, bones, rs.Bones.Length);
            Array.Copy(hb, 0, bones, rs.Bones.Length, hb.Length);
            if (rs.Bones.Length != baseN) { problems.Add(r.name + ": " + rs.Bones.Length + " bones but " + baseN + " bind poses"); return; }
            Mesh target;
            if (rs.InPlace)
            {
                target = rs.Base;
                rs.Saved = s.W;   // the clone's own weights (read before any change)
            }
            else
            {
                MeshData d;
                if (!_data.TryGetValue(rs.Base, out d)) return;
                target = MorphState.Clone(rs.Base, d);
                target.name = rs.Base.name + " (SfSoft)";
                _origOf[target] = rs.Base;
            }
            target.boneWeights = nw;
            target.bindposes = bind;
            r.bones = bones;
            if (r.sharedMesh != target) r.sharedMesh = target;
            rs.Mesh = target;
            rs.Used = true;
        }

        // Helper bind poses for one mesh: through the helper's bone when the mesh has it (with the mesh's current bind
        // pose for it, so MorphState's joint moves carry over), else through the reference space.
        void HelperBind(Rs rs, Matrix4x4[] bind)
        {
            int baseN = rs.BaseCount;
            for (int h = 0; h < _helpers.Count; h++)
            {
                var hp = _helpers[h];
                int bi = Array.IndexOf(rs.Bones, hp.Bone);
                bind[baseN + h] = bi >= 0 && bi < baseN ? Matrix4x4.Translate(-hp.SLocal) * bind[bi] : hp.BindRef * rs.ToRef;
            }
        }

        // MorphState moved its bind poses (joint weight changed): ours follow.
        void RefreshBind()
        {
            foreach (var kv in _rs)
            {
                var rs = kv.Value;
                if (!rs.Used || !rs.InPlace || rs.Mesh == null) continue;
                var bind = rs.Mesh.bindposes;
                if (bind.Length != rs.BaseCount + _helpers.Count) continue;
                HelperBind(rs, bind);
                rs.Mesh.bindposes = bind;
            }
        }

        // ---- colliders: the partner's hands and fingers, her forearms, hands and thighs ----

        void Colliders(Character c, List<Character> everyone, bool force)
        {
            bool changed = false;
            if (!_herCols)
            {
                _herCols = true;
                foreach (var s in new[] { "_L", "_R" })
                {
                    changed |= Own(c, "Hip" + s, "Knee" + s, 0.055f, s == "_L" ? "thighL" : "thighR", 0f);
                    changed |= Own(c, "Elbow" + s, "Wrist" + s, 0.032f, "forearm", 0f);
                    changed |= Own(c, "Wrist" + s, "MiddleFinger1" + s, 0.028f, "hand", 0.9f);
                }
            }
            // partner rigs: MovePlayerAndGirl.player / badGuy, and any other character in the scene that isn't her
            var roots = new HashSet<Transform>();
            foreach (var go in GameRig.Partners()) if (go != null) roots.Add(go.transform);
            if (everyone != null) foreach (var o in everyone) if (o != null && o.Root != null && o.Root != c.Root && !o.Female) roots.Add(o.Root);
            _cols.RemoveAll(x => x.C == null);
            foreach (var r in new List<Transform>(_partnerRoots))
                if (r == null || !roots.Contains(r))
                {
                    _partnerRoots.Remove(r);
                    foreach (var x in _cols) if (x.Owner == r && x.C != null) UnityEngine.Object.Destroy(x.C.gameObject);
                    _cols.RemoveAll(x => x.Owner == r);
                    changed = true;
                }
            foreach (var r in roots)
            {
                if (_partnerRoots.Contains(r) || r == c.Root || r.IsChildOf(c.Root)) continue;
                _partnerRoots.Add(r);
                int n = Hands(r, c.Root);
                changed |= n > 0;
            }
            if (changed && !force) AssignColliders();
        }

        bool Own(Character c, string from, string to, float radius, string kind, float extend)
        {
            Transform a, b;
            if (!c.Bones.TryGetValue(from, out a) || !c.Bones.TryGetValue(to, out b) || a == null || b == null) return false;
            var end = b.position + (b.position - a.position) * extend;
            var col = Capsule(a, a.position, end, radius, from);
            if (col == null) return false;
            _cols.Add(new Col { C = col, Owner = c.Root, Kind = kind });
            return true;
        }

        // Hands and fingers of one rig: a humanoid Animator's hand/finger bones, else bones named like hands, wrists, fingers.
        int Hands(Transform root, Transform her)
        {
            var made = new List<string>();
            Animator anim = null;   // not hers, if she happens to live under the same scene root
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
                if (a != null && !a.transform.IsChildOf(her) && (anim == null || (a.isHuman && !anim.isHuman))) anim = a;
            var segs = new List<KeyValuePair<Transform, Transform>>();   // bone -> next (null: extend from the parent)
            var palms = new List<KeyValuePair<Transform, Transform>>();
            if (anim != null && anim.isHuman && anim.avatar != null)
            {
                foreach (var side in new[] { true, false })
                {
                    var hand = anim.GetBoneTransform(side ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                    if (hand == null) continue;
                    var middle = anim.GetBoneTransform(side ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
                    palms.Add(new KeyValuePair<Transform, Transform>(hand, middle));
                    int first = (int)(side ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal);
                    for (int f = 0; f < 5; f++)
                        for (int j = 0; j < 3; j++)
                        {
                            var t = anim.GetBoneTransform((HumanBodyBones)(first + f * 3 + j));
                            var next = j < 2 ? anim.GetBoneTransform((HumanBodyBones)(first + f * 3 + j + 1)) : null;
                            if (t != null) segs.Add(new KeyValuePair<Transform, Transform>(t, next));
                        }
                }
            }
            if (palms.Count == 0)
            {
                var hands = new List<Transform>();
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    var n = Body.Norm(t.name);
                    if (t.childCount == 0 || t.name.StartsWith(Prefix) || t.IsChildOf(her) || !(n.Contains("hand") || n.Contains("wrist") || n.Contains("palm"))) continue;
                    if (IsFinger(n) || n.Contains("handle") || n.Contains("target") || n.Contains("ik") || n.Contains("twist")) continue;
                    bool under = false;
                    foreach (var h in hands) if (t.IsChildOf(h)) { under = true; break; }
                    if (!under) hands.Add(t);
                }
                foreach (var h in hands)
                {
                    Transform best = null; int most = -1;
                    for (int i = 0; i < h.childCount; i++)
                    {
                        var ch = h.GetChild(i);
                        int cnt = ch.GetComponentsInChildren<Transform>(true).Length;
                        if (Body.Norm(ch.name).Contains("middle")) { best = ch; break; }
                        if (cnt > most) { most = cnt; best = ch; }
                    }
                    palms.Add(new KeyValuePair<Transform, Transform>(h, best));
                    int k = 0;
                    foreach (var t in h.GetComponentsInChildren<Transform>(true))
                    {
                        if (t == h || t.name.StartsWith(Prefix) || ++k > 30) continue;
                        segs.Add(new KeyValuePair<Transform, Transform>(t, t.childCount > 0 ? t.GetChild(0) : null));
                    }
                }
            }
            foreach (var p in palms)
            {
                var end = p.Value != null ? p.Value.position : p.Key.position + p.Key.rotation * Vector3.up * 0.08f;
                var col = Capsule(p.Key, p.Key.position, end, 0.03f, p.Key.name + "_palm");
                if (col != null) { _cols.Add(new Col { C = col, Owner = root, Kind = "partner" }); made.Add(p.Key.name); }
            }
            foreach (var s in segs)
            {
                var t = s.Key;
                Vector3 a = t.position, b;
                if (s.Value != null) b = s.Value.position;
                else if (t.parent != null && (a - t.parent.position).sqrMagnitude > 1e-8f) b = a + (a - t.parent.position) * 0.8f;
                else continue;
                if ((b - a).sqrMagnitude > 0.08f * 0.08f) b = a + (b - a).normalized * 0.08f;   // not a finger
                var col = Capsule(t, a, b, 0.009f, t.name);
                if (col != null) _cols.Add(new Col { C = col, Owner = root, Kind = "partner" });
            }
            Plugin.Log.LogInfo("Soft zones: partner '" + root.name + "': " + (anim != null && anim.isHuman ? "humanoid" : "by bone names") + ", "
                               + palms.Count + " hands [" + string.Join(", ", made.ToArray()) + "], " + segs.Count + " finger segments");
            return palms.Count + segs.Count;
        }

        static bool IsFinger(string n)
        {
            foreach (var w in new[] { "finger", "thumb", "index", "middle", "ring", "pinky", "little" }) if (n.Contains(w)) return true;
            return false;
        }

        // A DynamicBone capsule between two world points, on a child of the bone with world scale ~1 (sizes in metres).
        Component Capsule(Transform bone, Vector3 a, Vector3 b, float r, string name)
        {
            var go = new GameObject(Prefix + "_col_" + name);
            go.transform.SetParent(bone, false);
            var dir = b - a; float len = dir.magnitude;
            go.transform.position = (a + b) * 0.5f;
            go.transform.rotation = len > 1e-5f ? Quaternion.FromToRotation(Vector3.up, dir / len) : bone.rotation;
            float s = Mathf.Abs(bone.lossyScale.x); if (s < 1e-6f) s = 1f;
            go.transform.localScale = Vector3.one / s;
            var c = go.AddComponent(_dbc);
            Set(c, "m_Radius", r);
            Set(c, "m_Height", len + 2f * r);
            Set(c, "m_Direction", 1);   // Y
            Set(c, "m_Center", Vector3.zero);
            return c;
        }

        // Which colliders each soft zone feels; the partner's hands also reach the game's own breast physics.
        void AssignColliders()
        {
            foreach (var h in _helpers)
            {
                if (h.Db == null) continue;
                var l = ListField(h.Db, _fColliders);
                l.Clear();
                foreach (var col in _cols)
                {
                    if (col.C == null) continue;
                    if (col.Kind == "thighL" && !(h.Zone.InnerThigh && h.Zone.Side < 0)) continue;   // a thigh presses the other one
                    if (col.Kind == "thighR" && !(h.Zone.InnerThigh && h.Zone.Side > 0)) continue;
                    l.Add(col.C);
                }
            }
            foreach (var kv in _gameAdds) if (IsCollider(kv.Value)) kv.Key.Remove(kv.Value);
            _gameAdds.RemoveAll(x => IsCollider(x.Value));
            if (_her == null) return;
            foreach (var g in GameDbs(_her))
            {
                var root = _fRoot.GetValue(g.Key) as Transform;
                if (root == null || !Body.Norm(root.name).Contains("breast")) continue;
                var l = ListField(g.Key, _fColliders);
                foreach (var col in _cols)
                    if (col.C != null && col.Kind == "partner" && !l.Contains(col.C)) { l.Add(col.C); _gameAdds.Add(new KeyValuePair<IList, object>(l, col.C)); }
            }
        }

        static bool IsCollider(object o) { return o is Component && !(o is Transform); }

        // Every frame after Body applies (bones carry our scales): the soft points' springs and contacts (0.7.0 stub).
        public void Simulate(float dt, List<SoftCapsule> caps, float softness, float spring, bool debug) { }
        public string Contacts = "";

        // ---- giving everything back ----

        // Meshes, bones, helpers and our entries in the game's lists; colliders too unless keepColliders.
        void Teardown(bool all)
        {
            foreach (var kv in _rs)
            {
                var r = kv.Key; var rs = kv.Value;
                if (!rs.Used) continue;
                if (r != null) r.bones = rs.Bones;
                if (rs.InPlace)
                {
                    if (rs.Mesh != null && rs.Saved != null)
                    {
                        rs.Mesh.boneWeights = rs.Saved;
                        var bp = rs.Mesh.bindposes;
                        if (bp.Length > rs.BaseCount) { var t = new Matrix4x4[rs.BaseCount]; Array.Copy(bp, t, rs.BaseCount); rs.Mesh.bindposes = t; }
                    }
                }
                else if (rs.Mesh != null)
                {
                    if (r != null && r.sharedMesh == rs.Mesh) r.sharedMesh = rs.Base;
                    _origOf.Remove(rs.Mesh);
                    UnityEngine.Object.Destroy(rs.Mesh);
                }
            }
            _rs.Clear();
            foreach (var kv in _gameAdds) if (kv.Key != null) kv.Key.Remove(kv.Value);
            _gameAdds.Clear();
            foreach (var h in _helpers) if (h.A != null) UnityEngine.Object.Destroy(h.A.gameObject);
            _helpers.Clear();
            _ours.Clear();
            _builtFor = null;
            if (!all) return;
            foreach (var col in _cols) if (col.C != null) UnityEngine.Object.Destroy(col.C.gameObject);
            _cols.Clear();
            _partnerRoots.Clear();
            _herCols = false;
            _data.Clear();
            _her = null;
        }

        public void Release()
        {
            if (_builtFor == null && _cols.Count == 0 && _rs.Count == 0) { Status = ""; return; }
            Teardown(true);
            _builtSoft = -1f;
            Status = "";
        }
    }
}
