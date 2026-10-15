using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // Soft zones v2 (stage 1 of contact deformation, no shaders): her glutes, inner/outer thighs, belly and breasts get a
    // few simulated spring points each, so hands and limbs push the skin in instead of passing through it, and the zones
    // lag and settle a little as she moves (springiness). The points are simulated here in C# (SoftSim.cs), not by the
    // game's DynamicBone: its pendulum turned pushes into sideways slides and its particles sat under the skin, so
    // contact began late and the skin never followed the hand.
    //
    // Per zone K points on the skin (the centre found along a body direction, the others on a ring around it). For every
    // bone that weights the skin near a point (at most two per point) a helper bone S is added directly under that
    // bone; Simulate moves S. Skin weights are split, not moved: a vertex's weight w on bone b becomes w(1-f) on b and
    // w f on S, where f is a smooth falloff from the zone's centre shared between the zone's two nearest points. S sits at
    // its rest place when nothing touches and springiness is 0, so she deforms exactly as without the mod.
    //
    // The meshes: her renderers show readable clones carrying the extra bones. With a converted character on, its
    // clones (MorphState) are extended in place; without one, the game's GPU-only meshes get our own clones (built
    // from the GPU readback) and MorphState treats them as the game's (Original()). Rebuilt when the game or the
    // morph swaps a mesh; everything (bones, meshes, weights, bind poses, the game's DynamicBone lists) is given back
    // on release. Helper bones are ours only: Body's trackers never write them. The contact capsules come from
    // VrHands; this file builds, SoftSim.cs simulates.
    internal partial class SoftZones
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
            public float Radius;                        // falloff radius of the vertex weights (m); the point ring is 0.55 of it
            public int K;                               // points: the centre and K-1 on the ring
            public bool InnerThigh;
        }

        static Zone Z(string id, string label, string from, string to, float at, Toward dir, int side, float radius, int k, params string[] region)
        {
            return new Zone { Id = id, Label = label, From = from, To = to, At = at, Dir = dir, Side = side, Region = region, Radius = radius, K = k };
        }

        static readonly Zone[] Zones = Build();

        static Zone[] Build()
        {
            var l = new List<Zone>();
            foreach (var s in new[] { 1, -1 })
            {
                string x = s > 0 ? "_L" : "_R", side = s > 0 ? "Left_" : "Right_";
                l.Add(Z("glute" + x, "glute", "Butt" + x, "Butt" + x, 0f, Toward.Back, s, 0.11f, 7, "Butt" + x + "*", "Hip" + x, "HipPart1" + x, "Root_M", "Ass_Outer"));
                l.Add(Z("thighOut" + x, "outer thigh", "Hip" + x, "Knee" + x, 0.35f, Toward.Out, s, 0.09f, 5, "Hip" + x + "*", "Butt" + x + "*"));
                var inner = Z("thighIn" + x, "inner thigh", "Hip" + x, "Knee" + x, 0.3f, Toward.In, s, 0.09f, 5, "Hip" + x + "*");
                inner.InnerThigh = true;
                l.Add(inner);
                l.Add(Z("breastFront" + x, "breast front", side + "LowerBreast", side + "LowerBreast", 0f, Toward.Forward, s, 0.06f, 3, side + "UpperBreast*"));
                l.Add(Z("breastSide" + x, "breast side", side + "LowerBreast", side + "LowerBreast", 0f, Toward.Out, s, 0.06f, 3, side + "UpperBreast*"));
            }
            l.Add(Z("belly", "belly", "Root_M", "Chest_M", 0.35f, Toward.Forward, 0, 0.10f, 5, "Root_M", "Spine1_M", "Vaginal_root*"));
            return l.ToArray();
        }

        // ---- game types (reflection: Assembly-CSharp isn't referenced) ----
        // Only the game's DynamicBone is still looked up: for the chain-bone rule (never add children to a bone its physics re-aims)
        // and the one-time survey. Our own points need none of it.

        static bool _typesLooked, _surveyed;
        static Type _db, _dbc;
        static FieldInfo _fRoot, _fColliders, _fExclusions;

        public static bool Available { get { return true; } }

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
            if (_fRoot == null || _fColliders == null) _db = null;
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
            if (_db == null) { Plugin.Log.LogInfo("Physics survey: the game has no DynamicBone class"); return; }
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
            if (_dbc != null)
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

        class Pt   // a spring point on the skin
        {
            public Zone Zone; public int ZoneIdx;
            public Vector3 RestRef;     // in the reference mesh's bind space
            public Vector3 NormalRef;   // inward, same space
            public int Main = -1;       // its heaviest helper (debug marker, status)
            public int Stamp;           // frame it was last counted as touched
        }

        class Helper   // S under a bone, standing for one point as that bone carries it
        {
            public Zone Zone; public int ZoneIdx;
            public Pt Point;
            public Transform Bone, S;
            public int BoneIdx;          // into _bones
            public Vector3 SLocal;       // rest place in Bone's space (S has no rotation or scale of its own)
            public Vector3 NLocal;       // inward normal in Bone's space
            public Matrix4x4 BindRef;    // S's bind pose in the reference mesh's space
            public float Total;          // how much skin it carries (vertex weight sum)
            // simulation (SoftSim.cs): world space
            public Vector3 Disp, Vel, PrevRest, R, N, Push;
            public bool Touched, Asleep = true, HasPrev, Bulged;
            public int LastCap;
        }

        readonly Dictionary<SkinnedMeshRenderer, Rs> _rs = new Dictionary<SkinnedMeshRenderer, Rs>();
        readonly List<Pt> _pts = new List<Pt>();
        readonly List<Helper> _helpers = new List<Helper>();
        readonly List<Transform> _bones = new List<Transform>();   // the distinct bones the helpers hang under
        readonly List<KeyValuePair<IList, object>> _gameAdds = new List<KeyValuePair<IList, object>>();  // items we put into the game's DynamicBone lists
        readonly Dictionary<Mesh, MeshData> _data = new Dictionary<Mesh, MeshData>();
        static readonly Dictionary<Mesh, Mesh> _origOf = new Dictionary<Mesh, Mesh>();          // our clone -> the game's mesh
        List<SkinnedMeshRenderer> _builtFor;
        Transform _her;
        float _builtSoft = -1f, _softChangedAt, _lastBuild = -10f, _nextCheck, _builtMorph;
        int _bindVersion;
        public string Status = "";

        // The game's mesh behind one of our clones (MorphState must clone the game's mesh, not ours).
        public static Mesh Original(Mesh m)
        {
            Mesh o;
            return m != null && _origOf.TryGetValue(m, out o) && o != null ? o : m;
        }

        static bool IsMorphClone(Mesh m) { return m != null && m.isReadable && m.name.EndsWith(" (SfMorph)"); }

        // Every frame, after MorphState.Update and before Body applies: on = Apply + Physics on + the game's woman.
        // Builds, rebuilds and maintains the zones; the springs are SoftSim.cs's Simulate.
        public void Update(Character c, bool on, float softness, float spring, List<Character> everyone)
        {
            if (!on || c.Root == null) { Release(); return; }
            Types();
            softness = Mathf.Clamp01(softness);

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
            }
            if (why != null)
            {
                Teardown(false);   // consistent right away (never their bones with a mesh that lacks our bind poses)
                if (Time.unscaledTime - _lastBuild < 0.5f) return;  // no rebuild storms; try again next frame
                _lastBuild = Time.unscaledTime;
                Build(c, softness, everyone, why);
            }
            if (MorphState.BindVersion != _bindVersion) { _bindVersion = MorphState.BindVersion; RefreshBind(); }
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

        class PF { public int[] P; public float[] W; }   // a vertex's share of the zones, per point (points of the zones it lies in)

        class Src   // a renderer's mesh as the zones see it (reference bind space)
        {
            public SkinnedMeshRenderer R; public Rs S;
            public Vector3[] P, N; public BoneWeight[] W; public Matrix4x4[] Bind;
            public bool Skin;
            public Dictionary<int, PF> F = new Dictionary<int, PF>();  // vertex -> its points and fractions
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

            // 4. zone points on the skin, then each vertex's share of them
            var regionOf = new Dictionary<Src, bool[][]>();
            foreach (var s in mapped) regionOf[s] = Regions(c, s.S.Bones);
            var found = new List<string>(); var missing = new List<string>();
            var zonePt0 = new int[Zones.Length]; var zonePtN = new int[Zones.Length];   // the zone's points in _pts
            var zoneQ = new Vector3?[Zones.Length];
            float fmax = Mathf.Lerp(0.6f, 1f, softness);

            // The skin vertex furthest along d within a tube of radius 'lateral' around the ray o + t d (t <= tmax), inside the zone's
            // body part and on its side.
            bool Pick(int z, Vector3 o, Vector3 d, float lateral, float tmax, bool bodyOnly, out Vector3 q, out Vector3 qn)
            {
                var zone = Zones[z];
                float bestT = 0f; bool hit = false;
                q = Vector3.zero; qn = d;
                foreach (var s in mapped)
                {
                    if (bodyOnly && !s.Skin) continue;
                    var reg = regionOf[s][z];
                    for (int i = 0; i < s.P.Length; i++)
                    {
                        if (RegionWeight(s.W[i], reg) < 0.4f) continue;
                        var p = s.P[i];
                        if (zone.Side != 0 && Vector3.Dot(p - center, left) * zone.Side * mpu < 0.005f) continue;
                        var v = p - o; float t = Vector3.Dot(v, d);
                        if (t <= bestT || t > tmax || (v - d * t).magnitude > lateral) continue;
                        bestT = t; q = p; qn = s.N != null ? s.N[i] : d; hit = true;
                    }
                }
                return hit;
            }

            for (int z = 0; z < Zones.Length; z++)
            {
                var zone = Zones[z];
                zonePt0[z] = _pts.Count;
                var a = pos(zone.From); var b = pos(zone.To);
                if (!a.HasValue || !b.HasValue) { missing.Add(zone.Id + " (no " + zone.From + "/" + zone.To + ")"); continue; }
                var o = Vector3.Lerp(a.Value, b.Value, zone.At);
                var d = zone.Dir == Toward.Back ? -fwd : zone.Dir == Toward.Forward ? fwd : zone.Dir == Toward.Out ? left * (zone.Side != 0 ? zone.Side : 1) : -left * (zone.Side != 0 ? zone.Side : 1);
                Vector3 q = Vector3.zero, qn = d; bool hit = false;
                for (int pass = 0; pass < 3 && !hit; pass++)   // body meshes, narrow ray; wider; any mesh
                    hit = Pick(z, o, d, (pass == 0 ? 0.03f : 0.06f) / mpu, float.MaxValue, pass < 2, out q, out qn);
                if (!hit) { missing.Add(zone.Id + " (no skin found)"); continue; }
                zoneQ[z] = q;
                var n = (qn + d).normalized;   // outward
                _pts.Add(new Pt { Zone = zone, ZoneIdx = z, RestRef = q, NormalRef = -n });
                // the others on a ring around the centre, re-projected onto the skin: a ray from 5 cm under the ring point along n
                if (zone.K > 1)
                {
                    var t1 = up - n * Vector3.Dot(up, n);
                    if (t1.sqrMagnitude < 1e-8f) t1 = fwd - n * Vector3.Dot(fwd, n);
                    t1 = t1.normalized;
                    var t2 = Vector3.Cross(n, t1);
                    float ringR = 0.55f * zone.Radius / mpu;
                    int m = zone.K - 1;
                    for (int i = 0; i < m; i++)
                    {
                        float ang = 2f * Mathf.PI * i / m;
                        var cand = q + (t1 * Mathf.Cos(ang) + t2 * Mathf.Sin(ang)) * ringR;
                        var ro = cand - n * (0.05f / mpu);
                        Vector3 pq = Vector3.zero, pn = Vector3.zero; bool ph = false;
                        for (int pass = 0; pass < 4 && !ph; pass++)   // narrow tube; wider; widest; any mesh
                            ph = Pick(z, ro, n, (pass == 0 ? 0.02f : pass == 1 ? 0.035f : 0.05f) / mpu, 0.10f / mpu, pass < 3, out pq, out pn);
                        if (!ph) continue;
                        bool dup = false;
                        for (int k = zonePt0[z]; k < _pts.Count; k++) if ((_pts[k].RestRef - pq).magnitude * mpu < 0.006f) { dup = true; break; }
                        if (dup) continue;
                        _pts.Add(new Pt { Zone = zone, ZoneIdx = z, RestRef = pq, NormalRef = -(pn + n).normalized });
                    }
                }
                zonePtN[z] = _pts.Count - zonePt0[z];
                found.Add(zone.Id);
            }
            var fz = new float[Zones.Length];
            var plist = new List<int>(); var wlist = new List<float>();
            foreach (var s in mapped)
            {
                var reg = regionOf[s];
                for (int i = 0; i < s.P.Length; i++)
                {
                    float total = 0f;
                    for (int z = 0; z < Zones.Length; z++)
                    {
                        fz[z] = 0f;
                        if (!zoneQ[z].HasValue) continue;
                        var zone = Zones[z];
                        float dist = (s.P[i] - zoneQ[z].Value).magnitude * mpu;
                        if (dist >= zone.Radius) continue;
                        float g = Mathf.Clamp01((RegionWeight(s.W[i], reg[z]) - 0.15f) / 0.35f);
                        if (zone.Side != 0) g *= Mathf.Clamp01(Vector3.Dot(s.P[i] - center, left) * zone.Side * mpu / 0.015f);
                        if (s.N != null) g *= Mathf.Clamp01(Vector3.Dot(s.N[i], -_pts[zonePt0[z]].NormalRef) * 1.5f + 0.5f);
                        float t = 1f - dist / zone.Radius;
                        float f = fmax * t * t * (3f - 2f * t) * g;
                        if (f < 0.005f) continue;
                        fz[z] = f; total += f;
                    }
                    if (total <= 0f) continue;
                    float scale = total > fmax ? fmax / total : 1f;
                    plist.Clear(); wlist.Clear();
                    for (int z = 0; z < Zones.Length; z++)
                    {
                        if (fz[z] <= 0f) continue;
                        float f = fz[z] * scale;
                        // shared between the zone's two nearest points, Shepard 1/(d+0.01)^2
                        int n1 = -1, n2 = -1; float d1 = float.MaxValue, d2 = float.MaxValue;
                        for (int k = zonePt0[z]; k < zonePt0[z] + zonePtN[z]; k++)
                        {
                            float dk = (s.P[i] - _pts[k].RestRef).magnitude * mpu;
                            if (dk < d1) { n2 = n1; d2 = d1; n1 = k; d1 = dk; } else if (dk < d2) { n2 = k; d2 = dk; }
                        }
                        if (n2 < 0) { plist.Add(n1); wlist.Add(f); continue; }
                        float w1 = 1f / ((d1 + 0.01f) * (d1 + 0.01f)), w2 = 1f / ((d2 + 0.01f) * (d2 + 0.01f));
                        plist.Add(n1); wlist.Add(f * w1 / (w1 + w2));
                        plist.Add(n2); wlist.Add(f * w2 / (w1 + w2));
                    }
                    s.F[i] = new PF { P = plist.ToArray(), W = wlist.ToArray() };
                }
            }

            // 5. helpers: per point, the (up to two) bones that carry its skin (a bone the game's physics chain re-aims is left alone)
            var gameDbs = GameDbs(c.Root);
            var noSplit = new HashSet<Transform>();
            // DynamicBone re-aims a chain bone only while it has at most one child: a helper under such a bone would
            // freeze its orientation, so only chain bones that already have several children get helpers.
            foreach (var g in gameDbs) foreach (var t in g.Value) if (t.childCount < 2) noSplit.Add(t);
            var carry = new Dictionary<long, float>();   // point * 4096 + bone index in bindRef order
            var boneIds = new Dictionary<Transform, int>(); var boneList = new List<Transform>();
            foreach (var s in mapped)
                foreach (var kv in s.F)
                {
                    var w = s.W[kv.Key]; var pf = kv.Value;
                    for (int k = 0; k < 4; k++)
                    {
                        int bi = k == 0 ? w.boneIndex0 : k == 1 ? w.boneIndex1 : k == 2 ? w.boneIndex2 : w.boneIndex3;
                        float bw = k == 0 ? w.weight0 : k == 1 ? w.weight1 : k == 2 ? w.weight2 : w.weight3;
                        if (bw <= 0f || bi >= s.S.Bones.Length || s.S.Bones[bi] == null) continue;
                        var bone = s.S.Bones[bi];
                        int id;
                        if (!boneIds.TryGetValue(bone, out id)) { id = boneList.Count; boneIds[bone] = id; boneList.Add(bone); }
                        for (int j = 0; j < pf.P.Length; j++) { long key = (long)pf.P[j] * 4096 + id; float cur; carry.TryGetValue(key, out cur); carry[key] = cur + bw * pf.W[j]; }
                    }
                }
            var helperOf = new Dictionary<long, int>();
            int skippedChain = 0;
            var zoneH0 = new int[Zones.Length]; var zoneH1 = new int[Zones.Length];
            for (int z = 0; z < Zones.Length; z++) { zoneH0[z] = -1; zoneH1[z] = -1; }
            for (int p = 0; p < _pts.Count; p++)
            {
                var cands = new List<KeyValuePair<int, float>>();
                foreach (var kv in carry) if (kv.Key / 4096 == p && kv.Value >= 0.5f) cands.Add(new KeyValuePair<int, float>((int)(kv.Key % 4096), kv.Value));
                cands.Sort((x, y) => y.Value.CompareTo(x.Value));
                int made = 0;
                foreach (var cand in cands)
                {
                    if (made >= 2) break;
                    var bone = boneList[cand.Key];
                    Matrix4x4 bp;
                    if (!bindRef.TryGetValue(bone, out bp)) continue;
                    if (noSplit.Contains(bone)) { skippedChain++; continue; }
                    var h = MakeHelper(_pts[p], bone, bp, gameDbs);
                    h.Total = cand.Value;
                    if (_pts[p].Main < 0) _pts[p].Main = _helpers.Count;
                    helperOf[(long)p * 4096 + cand.Key] = _helpers.Count;
                    _helpers.Add(h);
                    made++;
                }
            }
            var boneIdx = new Dictionary<Transform, int>();
            for (int i = 0; i < _helpers.Count; i++)
            {
                var h = _helpers[i];
                int bi;
                if (!boneIdx.TryGetValue(h.Bone, out bi)) { bi = _bones.Count; boneIdx[h.Bone] = bi; _bones.Add(h.Bone); }
                h.BoneIdx = bi;
                if (zoneH0[h.ZoneIdx] < 0) zoneH0[h.ZoneIdx] = i;
                zoneH1[h.ZoneIdx] = i + 1;
            }
            SimBuilt(zoneH0, zoneH1);

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
                var list = new List<KeyValuePair<int, float>>(24);
                foreach (var kv in s.F)
                {
                    var w = s.W[kv.Key]; var pf = kv.Value; list.Clear();
                    bool any = false;
                    for (int k = 0; k < 4; k++)
                    {
                        int bi = k == 0 ? w.boneIndex0 : k == 1 ? w.boneIndex1 : k == 2 ? w.boneIndex2 : w.boneIndex3;
                        float bw = k == 0 ? w.weight0 : k == 1 ? w.weight1 : k == 2 ? w.weight2 : w.weight3;
                        if (bw <= 0f) continue;
                        float stay = bw;
                        int id;
                        if (bi < s.S.Bones.Length && s.S.Bones[bi] != null && boneIds.TryGetValue(s.S.Bones[bi], out id))
                            for (int j = 0; j < pf.P.Length; j++)
                            {
                                int h;
                                if (!helperOf.TryGetValue((long)pf.P[j] * 4096 + id, out h)) continue;
                                Add(list, s.S.Bones.Length + h, bw * pf.W[j]);
                                stay -= bw * pf.W[j];
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

            _builtMorph = MorphWeight();
            _bindVersion = MorphState.BindVersion;
            int withHelpers = 0;
            foreach (var pt in _pts) if (pt.Main >= 0) withHelpers++;
            Status = _helpers.Count > 0
                ? "on: " + found.Count + " zones, " + _pts.Count + " points, " + _helpers.Count + " helper bones on " + used + " meshes"
                : "off: no zone found skin to soften (see the log)";
            var sb = new StringBuilder();
            sb.Append("Soft zones on '").Append(c.Key).Append("' (").Append(why).Append("): ").Append(found.Count).Append(" zones, ").Append(_pts.Count).Append(" points (")
              .Append(withHelpers).Append(" with helpers), ").Append(_helpers.Count).Append(" helper bones on ").Append(_bones.Count).Append(" bones, ")
              .Append(used).Append(" meshes [").Append(string.Join(", ", meshNames.ToArray())).Append("]; softness ").Append(softness.ToString("0.00")).Append(", ")
              .Append(mpu.ToString("0.####")).Append(" m/unit, morph ").Append(_builtMorph.ToString("0.00"))
              .Append(", built in ").Append(((Time.realtimeSinceStartup - t0) * 1000f).ToString("0")).Append(" ms");
            if (skippedChain > 0) sb.Append("; ").Append(skippedChain).Append(" bone slots left alone (the game's physics re-aims them)");
            if (missing.Count > 0) sb.Append("\n  zones not placed: ").Append(string.Join(", ", missing.ToArray()));
            sb.Append("\n  zones:");
            for (int z = 0; z < Zones.Length; z++)
                if (zoneQ[z].HasValue) sb.Append(' ').Append(Zones[z].Id).Append('(').Append(zonePtN[z]).Append(')');
            sb.Append("\n  helpers (zone#point@bone, skin carried):");
            foreach (var h in _helpers) sb.Append(' ').Append(h.Zone.Id).Append('#').Append(_pts.IndexOf(h.Point) - zonePt0[h.ZoneIdx]).Append('@').Append(h.Bone.name).Append('(').Append(h.Total.ToString("0.0")).Append(')');
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
            if (_db == null) return d;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(_db))
            {
                var db = o as Component;
                if (db == null) continue;
                var root = _fRoot.GetValue(db) as Transform;
                if (root == null || !root.IsChildOf(her)) continue;
                d[db] = new List<Transform>(root.GetComponentsInChildren<Transform>(true));
            }
            return d;
        }

        // S directly under the bone at the point's rest place; Simulate moves it. Its bind pose is a translation of the bone's,
        // so at rest it skins exactly like the bone.
        Helper MakeHelper(Pt pt, Transform bone, Matrix4x4 bp, Dictionary<Component, List<Transform>> gameDbs)
        {
            var sLocal = bp.MultiplyPoint3x4(pt.RestRef);
            var nLocal = bp.MultiplyVector(pt.NormalRef).normalized;
            var gs = new GameObject(Prefix + "_" + pt.Zone.Id + "_" + bone.name);
            gs.transform.SetParent(bone, false);
            gs.transform.localPosition = sLocal; gs.transform.localRotation = Quaternion.identity; gs.transform.localScale = Vector3.one;
            // the game's chains that hold this bone must not pick our helper up if they ever rebuild
            if (_fExclusions != null)
                foreach (var g in gameDbs)
                    if (g.Value.Contains(bone)) { var l = ListField(g.Key, _fExclusions); l.Add(gs.transform); _gameAdds.Add(new KeyValuePair<IList, object>(l, gs.transform)); }
            return new Helper { Zone = pt.Zone, ZoneIdx = pt.ZoneIdx, Point = pt, Bone = bone, S = gs.transform, SLocal = sLocal, NLocal = nLocal, BindRef = Matrix4x4.Translate(-sLocal) * bp };
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

        // ---- giving everything back ----

        // Meshes, bones, helpers and our entries in the game's lists; the per-session caches too when 'all'.
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
            foreach (var h in _helpers) if (h.S != null) UnityEngine.Object.Destroy(h.S.gameObject);
            _helpers.Clear();
            _pts.Clear();
            _bones.Clear();
            SimReset();
            _builtFor = null;
            Contacts = "";
            if (!all) return;
            _data.Clear();
            _her = null;
        }

        public void Release()
        {
            if (_builtFor == null && _rs.Count == 0) { Status = ""; Contacts = ""; return; }
            Teardown(true);
            _builtSoft = -1f;
            Status = "";
        }
    }
}
