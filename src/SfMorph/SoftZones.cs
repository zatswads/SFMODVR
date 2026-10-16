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
    // Per zone K points on the skin (the centre found along a body direction, the others on rings around it: the 0.7.0 single ring at
    // Performance, the dense 131-point layout at Balanced/Fidelity, see RingsFor; Fidelity also subdivides the body meshes). For every
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
    // 0.8.0: a fresh fast contact on a soft zone (SoftSim -> SoftZones.Impacts -> Ripple.Collect). P = contact point on the skin (world),
    // Dir = the capsule's motion direction into her (unit, world), Speed = approach speed along the skin's inward normal (m/s),
    // Bone = the helper's bone (the wave follows it), Zone = zone index (SoftZones.ZoneId), SrcId = CapSource.Id (0 none), By = source name.
    internal struct SoftImpact
    {
        public Vector3 P, Dir;
        public float Speed;
        public Transform Bone;
        public int Zone, SrcId;
        public string By;
        public bool Thrust;   // 0.11.12: a stroke of his hips (Speed = the stroke's peak closing speed), sized by [Physics] RippleThrustAmt
    }

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

        // ---- quality presets (0.7.2) ----
        // 0 Performance = the 0.7.0 layout (centre + one ring of K-1 at 0.55 R). 1 Balanced / 2 Fidelity = the dense layout (same points; 2 also
        // subdivides the body meshes): glute 1+6+12, thighs/belly 1+4+8, breast 1+6 = 131 points for the whole body. Rings are fractions of the
        // zone's Radius; the thigh rings are stretched 1.6:1 along the bone (area kept: sqrt(1.6) along, 1/sqrt(1.6) across).

        struct Ring { public int N; public float Frac, Phase; public Ring(int n, float f, float ph) { N = n; Frac = f; Phase = ph; } }

        static Ring[] RingsFor(Zone z, int q)
        {
            if (q <= 0) return z.K > 1 ? new[] { new Ring(z.K - 1, 0.55f, 0f) } : new Ring[0];
            if (z.Label == "glute") return new[] { new Ring(6, 0.35f, 0f), new Ring(12, 0.7f, Mathf.PI / 12f) };
            if (z.Label == "breast front" || z.Label == "breast side") return new[] { new Ring(6, 0.5f, 0f) };
            return new[] { new Ring(4, 0.35f, 0f), new Ring(8, 0.7f, Mathf.PI / 8f) };   // thighs, belly
        }

        static bool Stretched(Zone z, int q) { return q > 0 && (z.Label == "outer thigh" || z.Label == "inner thigh"); }
        const float Stretch = 1.2649111f;   // sqrt(1.6)
        public const int MaxHelpers = 300;
        static readonly string[] QualityNames = { "Performance", "Balanced", "Fidelity" };
        const float HardFloor = 0.25f;   // 0.7.7: least GPU contact mask on any vertex of a mapped mesh (x the softness limit: ~8 mm at softness 0.5, like the physics hands' 8 mm hard inset)

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
            public Subdiv.Undo Undo;   // Fidelity: the subdivision of Mesh (in place), given back exactly in Teardown
            public Mesh Orig;          // OwnMesh: the game's mesh behind our readable (subdivided) clone Mesh
            public bool OwnMesh;       // Mesh is our own clone made for the subdivision (destroyed in Teardown)
            public float[] Mask;       // 0.7.3: per vertex of Mesh, 0..1 share of the zones (GPU contact deformer); null when not augmented
            public float[] Zone;       // 0.8.0: Mask before the hard-skin floor (the zones' own share only; ContactBake's soft cap)
        }

        class Pt   // a spring point on the skin
        {
            public Zone Zone; public int ZoneIdx;
            public Vector3 RestRef;     // in the reference mesh's bind space
            public Vector3 NormalRef;   // inward, same space
            public int Main = -1;       // its heaviest helper (debug marker, status)
            public int Stamp;           // frame it was last counted as touched
            public ulong SrcMask;       // 0.7.7: bit = source id that touched it this frame (counted once per source, whichever helper)
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
            public Vector3 Sag;          // 0.7.3: the spring's equilibrium offset from R (world): gravity's pull on the zone, zero without gravity
            public bool Touched, Asleep = true, HasPrev, Bulged;
            public int LastCap;
            public int HandMask;         // physics hands that touched it this frame: 1 left, 2 right
            public ulong SrcMask;        // 0.7.7: bit = id of each capsule source that touched it this frame (bit 0: a capsule with no source)

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
        int _builtQ = -1;          // the quality the current build was asked for
        int _bq;                   // the quality Build is working with (Read reads it)
        int _subdivN;              // body meshes subdivided by this build
        bool _nippleNoted;         // the Fidelity note about an unsubdivided nipple mesh was logged (once per session)
        bool _subdivFailed;        // Subdiv threw once: no more subdivision this session (Fidelity drops to Balanced's meshes)
        public string Status = "";

        // The game's mesh behind one of our clones (MorphState must clone the game's mesh, not ours).
        // 0.7.2: how deep each physics hand is into her skin (0..1 of the limit), for the hands' force cap; per zone contact.
        // Smoothed (0.1 s) from the deepest dent each SfPhys* hand makes (SoftSim.cs); 0 when that hand touches nothing or the zones are off.
        public static float LeftResist, RightResist;
        // 0.7.3: the GPU contact deformer's view of a build. BuildVersion is bumped on every Build and Teardown (a consumer re-syncs when it
        // changes). MaskFor(r): per vertex of the mesh r shows now (our augmented / subdivided clone, in its vertex order) the share 0..1 of the
        // zones, min(1, sum of the vertex's zone fractions / fmax); 0 outside the zones; null when r isn't one of ours or isn't augmented.
        // GpuRenderers: her augmented renderers that have a mask (body meshes and clothes alike; the consumer filters by vertex layout).
        public int BuildVersion;
        public List<SkinnedMeshRenderer> GpuRenderers = new List<SkinnedMeshRenderer>();

        public float[] MaskFor(SkinnedMeshRenderer r)
        {
            Rs rs;
            if (r == null || !_rs.TryGetValue(r, out rs) || !rs.Used || rs.Mask == null || r.sharedMesh != rs.Mesh) return null;
            return rs.Mask;
        }

        // True while any point of a zone whose Id starts with the prefix (e.g. "thighOut_L", "glute", "thigh") is touched by anything.
        // Per frame state from the last Simulate; allocation-free.
        // 0.8.0: like MaskFor, but without the 0.7.7 hard-skin floor: 0 outside the zones (GpuContact v2 caps bony skin by bone class instead).
        public float[] ZoneFor(SkinnedMeshRenderer r)
        {
            Rs rs;
            if (r == null || !_rs.TryGetValue(r, out rs) || !rs.Used || rs.Zone == null || r.sharedMesh != rs.Mesh) return null;
            return rs.Zone;
        }

        // 0.8.0: Body sets this every frame from [Physics] PartnerBody: off = the "partnerBody" capsules (his pelvis, thighs) touch nothing.
        public static bool PartnerBody = true;

        // 0.8.0: fresh fast contacts of the last Simulate (SoftSim's contact loop writes them, Ripple.Collect reads them after Simulate).
        // At most Impacts.Length per frame (the fastest kept); ImpactCount is reset at the start of every Simulate.
        public readonly SoftImpact[] Impacts = new SoftImpact[4];
        public int ImpactCount;

        // The zone id of a zone index (Impact.Zone), "" when out of range.
        public static string ZoneId(int zone) { return zone >= 0 && zone < Zones.Length ? Zones[zone].Id : ""; }

        public bool Touching(string zoneIdPrefix)
        {
            if (_zCnt == null || string.IsNullOrEmpty(zoneIdPrefix)) return false;
            for (int z = 0; z < _zCnt.Length; z++)
                if (_zCnt[z] > 0 && Zones[z].Id.StartsWith(zoneIdPrefix, StringComparison.Ordinal)) return true;
            return false;
        }

        public static Mesh Original(Mesh m)
        {
            Mesh o;
            return m != null && _origOf.TryGetValue(m, out o) && o != null ? o : m;
        }

        static bool IsMorphClone(Mesh m) { return m != null && m.isReadable && m.name.EndsWith(" (SfMorph)"); }

        // Every frame, after MorphState.Update and before Body applies: on = Apply + Physics on + the game's woman.
        // Builds, rebuilds and maintains the zones; the springs are SoftSim.cs's Simulate.
        public void Update(Character c, bool on, float softness, float spring, List<Character> everyone, int quality = 1)
        {
            if (!on || c.Root == null) { Release(); return; }
            Types();
            softness = Mathf.Clamp01(softness);
            quality = Mathf.Clamp(quality, 0, 2);

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
            if (why == null && quality != _builtQ) why = "quality " + QualityNames[Mathf.Max(_builtQ, 0)] + " -> " + QualityNames[quality];
            if (why == null && Time.unscaledTime >= _nextCheck)
            {
                _nextCheck = Time.unscaledTime + 2f;
                if (Mathf.Abs(MorphWeight() - _builtMorph) > 0.1f || ShapesMoved()) why = "the character shape's weight changed";
            }
            if (why != null)
            {
                Teardown(false);   // consistent right away (never their bones with a mesh that lacks our bind poses)
                if (Time.unscaledTime - _lastBuild < 0.5f) return;  // no rebuild storms; try again next frame
                _lastBuild = Time.unscaledTime;
                Build(c, softness, everyone, why, quality);
            }
            if (MorphState.BindVersion != _bindVersion) { _bindVersion = MorphState.BindVersion; RefreshBind(); }
        }

        // The rest shape's morph weight: on MorphState's clones the sum of every shape that changes the rest shape (character body/face and the
        // body sliders' +/- shapes; never the joint correctives, which follow the pose); else shape 0 (0.6.x clones).
        float MorphWeight()
        {
            foreach (var kv in _rs)
                if (kv.Key != null && kv.Value.Used && kv.Value.Base != null && kv.Value.Base.blendShapeCount > 0)
                    return IsMorphClone(kv.Key.sharedMesh) && MorphState.ShapeLayout(kv.Key.sharedMesh).Count > 0 ? MorphState.NonJcmSum(kv.Key) : kv.Key.GetBlendShapeWeight(0) / 100f;
            return 0f;
        }

        // The rest-shape (non-corrective) blendshape weights of every used clone, in _rs order. Built with the zones; compared every 2 s:
        // one slider moving by > 0.1 rebuilds, even when another moved the other way (the sum alone would miss that).
        readonly List<float> _builtShapes = new List<float>(), _shapesNow = new List<float>();

        void ShapeWeights(List<float> into)
        {
            into.Clear();
            foreach (var kv in _rs)
            {
                if (kv.Key == null || !kv.Value.Used || !IsMorphClone(kv.Key.sharedMesh)) continue;
                var shapes = MorphState.ShapeLayout(kv.Key.sharedMesh);
                for (int i = 0; i < shapes.Count; i++)
                    if (shapes[i].Kind != ShapeKind.Jcm) into.Add(MorphState.ShapeWeight(kv.Key, shapes[i]));
            }
        }

        bool ShapesMoved()
        {
            ShapeWeights(_shapesNow);
            if (_shapesNow.Count != _builtShapes.Count) return true;
            for (int i = 0; i < _shapesNow.Count; i++) if (Mathf.Abs(_shapesNow[i] - _builtShapes[i]) > 0.1f) return true;
            return false;
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

        // Build gives up after Read() may have subdivided her meshes: give them back, but stay "built" for these renderers so Update does not retry every 0.5 s.
        void GiveUp(Character c)
        {
            Teardown(false);
            _builtFor = new List<SkinnedMeshRenderer>(c.Renderers);
        }

        void Build(Character c, float softness, List<Character> everyone, string why, int quality)
        {
            var t0 = Time.realtimeSinceStartup;
            BuildVersion++;
            GpuRenderers.Clear();
            _builtQ = quality; _bq = quality; _subdivN = 0;
            int qual = quality;   // 0 Performance, 1 Balanced, 2 Fidelity (the points are the same for 1 and 2)
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
            if (srcs.Count == 0) { GiveUp(c); Status = "off: none of her meshes could be read" + (problems.Count > 0 ? " (" + problems[0] + ")" : ""); Log(c, why, t0, problems); return; }

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
                GiveUp(c);
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
            var zoneSpace = new float[Zones.Length];   // spacing of each zone's outermost ring (m): the Gaussian share's sigma
            float fmax = Mathf.Lerp(0.6f, 1f, softness);

            // Per mesh and zone, the vertices that can host a point (inside the zone's body part, on its side): the ray picks below
            // then scan these only (the dense 131-point layouts and Fidelity's subdivided meshes would otherwise rescan every vertex).
            var candOf = new Dictionary<Src, int[][]>();
            var tmpIdx = new List<int>();
            foreach (var s in mapped)
            {
                var arr = new int[Zones.Length][];
                for (int z = 0; z < Zones.Length; z++)
                {
                    var zone = Zones[z]; var reg = regionOf[s][z];
                    tmpIdx.Clear();
                    for (int i = 0; i < s.P.Length; i++)
                    {
                        if (RegionWeight(s.W[i], reg) < 0.4f) continue;
                        if (zone.Side != 0 && Vector3.Dot(s.P[i] - center, left) * zone.Side * mpu < 0.005f) continue;
                        tmpIdx.Add(i);
                    }
                    arr[z] = tmpIdx.ToArray();
                }
                candOf[s] = arr;
            }

            // The skin vertex furthest along d within a tube of radius 'lateral' around the ray o + t d (t <= tmax), inside the zone's
            // body part and on its side.
            bool Pick(int z, Vector3 o, Vector3 d, float lateral, float tmax, bool bodyOnly, out Vector3 q, out Vector3 qn)
            {
                float bestT = 0f; bool hit = false;
                q = Vector3.zero; qn = d;
                foreach (var s in mapped)
                {
                    if (bodyOnly && !s.Skin) continue;
                    var cand = candOf[s][z];
                    for (int ci = 0; ci < cand.Length; ci++)
                    {
                        int i = cand[ci];
                        var p = s.P[i];
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
                Vector3 q0 = Vector3.zero, qn = d; bool hit = false;
                for (int pass = 0; pass < 3 && !hit; pass++)   // body meshes, narrow ray; wider; any mesh
                    hit = Pick(z, o, d, (pass == 0 ? 0.03f : 0.06f) / mpu, float.MaxValue, pass < 2, out q0, out qn);
                if (!hit) { missing.Add(zone.Id + " (no skin found)"); continue; }
                zoneQ[z] = q0;
                var n = (qn + d).normalized;   // outward
                _pts.Add(new Pt { Zone = zone, ZoneIdx = z, RestRef = q0, NormalRef = -n });
                // the others on rings around the centre, re-projected onto the skin: a ray from 5 cm under the ring point along n
                var rings = RingsFor(zone, qual);
                if (rings.Length > 0)
                {
                    bool stretched = Stretched(zone, qual);
                    var along = stretched ? b.Value - a.Value : up;   // the thigh rings are stretched along the bone
                    var t1 = along - n * Vector3.Dot(along, n);
                    if (t1.sqrMagnitude < 1e-8f) t1 = up - n * Vector3.Dot(up, n);
                    if (t1.sqrMagnitude < 1e-8f) t1 = fwd - n * Vector3.Dot(fwd, n);
                    t1 = t1.normalized;
                    var t2 = Vector3.Cross(n, t1);
                    float sAlong = stretched ? Stretch : 1f, sAcross = stretched ? 1f / Stretch : 1f;
                    foreach (var ring in rings)
                    {
                        float ringR = ring.Frac * zone.Radius / mpu;
                        for (int i = 0; i < ring.N; i++)
                        {
                            float ang = ring.Phase + 2f * Mathf.PI * i / ring.N;
                            var cand = q0 + (t1 * (Mathf.Cos(ang) * sAlong) + t2 * (Mathf.Sin(ang) * sAcross)) * ringR;
                            var ro = cand - n * (0.05f / mpu);
                            Vector3 pq = Vector3.zero, pn = Vector3.zero; bool ph = false;
                            for (int pass = 0; pass < 4 && !ph; pass++)   // narrow tube; wider; widest; any mesh (the dense layouts start narrower)
                                ph = Pick(z, ro, n, (qual > 0 ? (pass == 0 ? 0.012f : pass == 1 ? 0.022f : 0.035f) : (pass == 0 ? 0.02f : pass == 1 ? 0.035f : 0.05f)) / mpu, 0.10f / mpu, pass < 3, out pq, out pn);
                            if (!ph) continue;
                            bool dup = false;
                            for (int k = zonePt0[z]; k < _pts.Count; k++) if ((_pts[k].RestRef - pq).magnitude * mpu < 0.006f) { dup = true; break; }
                            if (dup) continue;
                            _pts.Add(new Pt { Zone = zone, ZoneIdx = z, RestRef = pq, NormalRef = -(pn + n).normalized });
                        }
                    }
                    var outer = rings[rings.Length - 1];
                    zoneSpace[z] = outer.N >= 2 ? outer.Frac * zone.Radius * 2f * Mathf.Sin(Mathf.PI / outer.N) : 0.55f * zone.Radius;
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
                        int n1 = -1, n2 = -1, n3 = -1; float d1 = float.MaxValue, d2 = float.MaxValue, d3 = float.MaxValue;
                        for (int k = zonePt0[z]; k < zonePt0[z] + zonePtN[z]; k++)
                        {
                            float dk = (s.P[i] - _pts[k].RestRef).magnitude * mpu;
                            if (dk < d1) { n3 = n2; d3 = d2; n2 = n1; d2 = d1; n1 = k; d1 = dk; }
                            else if (dk < d2) { n3 = n2; d3 = d2; n2 = k; d2 = dk; }
                            else if (dk < d3) { n3 = k; d3 = dk; }
                        }
                        if (n1 < 0) continue;
                        if (qual > 0)
                        {
                            // Balanced/Fidelity: the 3 nearest points with a Gaussian kernel, sigma = the zone's ring spacing (relative to the nearest, so it can't underflow)
                            float sp = zoneSpace[z] > 1e-4f ? zoneSpace[z] : 0.03f, inv = 1f / (2f * sp * sp);
                            float g2 = n2 >= 0 ? Mathf.Exp(-(d2 * d2 - d1 * d1) * inv) : 0f, g3 = n3 >= 0 ? Mathf.Exp(-(d3 * d3 - d1 * d1) * inv) : 0f;
                            float gs = 1f + g2 + g3;
                            plist.Add(n1); wlist.Add(f / gs);
                            if (g2 > 0.002f) { plist.Add(n2); wlist.Add(f * g2 / gs); }
                            if (g3 > 0.002f) { plist.Add(n3); wlist.Add(f * g3 / gs); }
                            continue;
                        }
                        // Performance (0.7.0): shared between the zone's two nearest points, Shepard 1/(d+0.01)^2
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
            int skippedChain = 0, overCap = 0;
            // A point's skin share is smaller in the dense layouts (more points split the same vertices), so the bar for a bone to count is lower there.
            float carryMin = qual > 0 ? 0.2f : 0.5f;
            var zoneH0 = new int[Zones.Length]; var zoneH1 = new int[Zones.Length];
            for (int z = 0; z < Zones.Length; z++) { zoneH0[z] = -1; zoneH1[z] = -1; }
            for (int p = 0; p < _pts.Count; p++)
            {
                var cands = new List<KeyValuePair<int, float>>();
                foreach (var kv in carry) if (kv.Key / 4096 == p && kv.Value >= carryMin) cands.Add(new KeyValuePair<int, float>((int)(kv.Key % 4096), kv.Value));
                cands.Sort((x, y) => y.Value.CompareTo(x.Value));
                int made = 0;
                foreach (var cand in cands)
                {
                    if (made >= 2) break;
                    var bone = boneList[cand.Key];
                    Matrix4x4 bp;
                    if (!bindRef.TryGetValue(bone, out bp)) continue;
                    if (noSplit.Contains(bone)) { skippedChain++; continue; }
                    if (_helpers.Count >= MaxHelpers) { overCap++; continue; }
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
            var zoneHN = new int[Zones.Length];
            for (int z = 0; z < Zones.Length; z++) zoneHN[z] = zoneH0[z] < 0 ? 0 : zoneH1[z] - zoneH0[z];

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
                int nb = s.S.Bones.Length;
                var hbi = new int[_helpers.Count];   // each helper's bone as this mesh numbers it
                for (int h = 0; h < hbi.Length; h++) hbi[h] = Array.IndexOf(s.S.Bones, _helpers[h].Bone);
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
                    // More than 4 influences (the dense layouts share a vertex between up to 3 points x 2 bones): fold the smallest helpers back
                    // into their own bone, which skins identically at rest, instead of dropping weight (that would bend her at rest).
                    while (list.Count > 4)
                    {
                        int mi = -1;
                        for (int k = 0; k < list.Count; k++) if (list[k].Key >= nb && (mi < 0 || list[k].Value < list[mi].Value)) mi = k;
                        if (mi < 0) break;
                        var e = list[mi]; list.RemoveAt(mi);
                        int pb = hbi[e.Key - nb];
                        if (pb >= 0) Add(list, pb, e.Value);
                    }
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
                if (s.S.Used)
                {
                    used++; meshNames.Add(s.R.name + "(" + changed + ")");
                    // the GPU mask: each vertex's zone fractions (s.F is keyed by this mesh's own vertex order, subdivision included)
                    var mask = new float[s.P.Length];
                    foreach (var kv in s.F)
                    {
                        float sumF = 0f;
                        for (int j = 0; j < kv.Value.W.Length; j++) sumF += kv.Value.W[j];
                        mask[kv.Key] = Mathf.Min(1f, sumF / fmax);
                    }
                    // 0.7.7 hard-skin floor: every vertex of a BODY mesh gets at least HardFloor, so a hand presses any skin out of its way
                    // (zones keep their larger share). GPU only (MaskFor): the soft points (SoftSim) are built from s.F, not from this array.
                    // Body meshes only (0.7.8): thin parts like the nipple caps would be pushed through at their rim. s.Skin is the "body" test on the
                    // mesh name as Read saw it; MorphState's and our own clones keep the game's name plus a suffix, so it holds with a character on.
                    s.S.Zone = (float[])mask.Clone();   // 0.8.0: the share before the floor (ZoneFor)
                    if (s.Skin)
                        for (int j = 0; j < mask.Length; j++) if (mask[j] < HardFloor) mask[j] = HardFloor;
                    if (s.S.Mesh != null && s.S.Mesh.vertexCount == mask.Length) { s.S.Mask = mask; GpuRenderers.Add(s.R); }
                    else problems.Add(s.R.name + ": GPU mask size " + mask.Length + " != mesh vertices " + (s.S.Mesh != null ? s.S.Mesh.vertexCount : 0));
                }
            }

            // 0.7.7 (A1.j): Subdiv only touches meshes named "body", so at Fidelity the PN-curved body rim meets the nipple cap mesh's straight edge
            if (_bq >= 2 && !_nippleNoted)
                foreach (var s in mapped)
                {
                    if (!s.S.Used || s.S.Undo != null || s.R == null || s.R.sharedMesh == null) continue;
                    string mn = s.R.sharedMesh.name;
                    if (mn.IndexOf("nipple", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    _nippleNoted = true;
                    Plugin.Log.LogInfo("Soft zones: Fidelity: '" + mn + "' is not subdivided; a hairline step at the areola rim is expected");
                    break;
                }

            _builtMorph = MorphWeight();
            ShapeWeights(_builtShapes);
            _bindVersion = MorphState.BindVersion;
            int withHelpers = 0;
            foreach (var pt in _pts) if (pt.Main >= 0) withHelpers++;
            Status = _helpers.Count > 0
                ? "on (" + QualityNames[qual] + "): " + found.Count + " zones, " + _pts.Count + " points, " + _helpers.Count + " helper bones on " + used + " meshes" + (_subdivN > 0 ? ", " + _subdivN + " subdivided" : "")
                : "off: no zone found skin to soften (see the log)";
            var sb = new StringBuilder();
            sb.Append("Soft zones on '").Append(c.Key).Append("' (").Append(why).Append(", quality ").Append(QualityNames[qual]).Append("): ").Append(found.Count).Append(" zones, ").Append(_pts.Count).Append(" points (")
              .Append(withHelpers).Append(" with helpers), ").Append(_helpers.Count).Append(" helper bones on ").Append(_bones.Count).Append(" bones, ")
              .Append(used).Append(" meshes [").Append(string.Join(", ", meshNames.ToArray())).Append("]; softness ").Append(softness.ToString("0.00")).Append(", ")
              .Append(mpu.ToString("0.####")).Append(" m/unit, morph ").Append(_builtMorph.ToString("0.00"))
              .Append(", built in ").Append(((Time.realtimeSinceStartup - t0) * 1000f).ToString("0")).Append(" ms");
            if (_subdivN > 0) sb.Append("; ").Append(_subdivN).Append(" body meshes subdivided");
            sb.Append("; GPU mask floor ").Append(HardFloor.ToString("0.00")).Append(" (body meshes only)");
            if (overCap > 0) sb.Append("; ").Append(overCap).Append(" helper slots over the cap of ").Append(MaxHelpers).Append(" dropped");
            if (skippedChain > 0) sb.Append("; ").Append(skippedChain).Append(" bone slots left alone (the game's physics re-aims them)");
            if (missing.Count > 0) sb.Append("\n  zones not placed: ").Append(string.Join(", ", missing.ToArray()));
            sb.Append("\n  zones (points/helpers):");
            for (int z = 0; z < Zones.Length; z++)
                if (zoneQ[z].HasValue) sb.Append(' ').Append(Zones[z].Id).Append('(').Append(zonePtN[z]).Append('/').Append(zoneHN[z]).Append(')');
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
            bool body = m.name.IndexOf("body", StringComparison.OrdinalIgnoreCase) >= 0;
            if (_bq >= 2 && body && !_subdivFailed) { Subdivide(r, rs, problems); m = r.sharedMesh; }   // BEFORE the mesh is read, so the weights are computed on the dense mesh
            var s = new Src { R = r, S = rs, Skin = body };
            if (IsMorphClone(m) || rs.OwnMesh)
            {
                rs.InPlace = true;
                s.P = m.vertices; s.N = m.normals; s.W = m.boneWeights; s.Bind = m.bindposes;
                if (s.N != null && s.N.Length != s.P.Length) s.N = null;
                if (m.blendShapeCount > 0)
                {
                    // the rest shape: every non-corrective shape at its current weight (MorphState's layout; 0.6.x clones: shape 0 only);
                    // normals accumulated, normalised once
                    var shapes = MorphState.ShapeLayout(m);
                    int ns = shapes.Count > 0 ? shapes.Count : 1;
                    Vector3[] dv = null, dn = null;
                    bool moved = false;
                    for (int k = 0; k < ns; k++)
                    {
                        int idx = shapes.Count > 0 ? shapes[k].Index : 0;
                        if (shapes.Count > 0 && shapes[k].Kind == ShapeKind.Jcm) continue;
                        if (idx < 0 || idx >= m.blendShapeCount) continue;
                        float w = r.GetBlendShapeWeight(idx) / 100f;
                        if (!(w > 0f)) continue;
                        if (dv == null) { dv = new Vector3[s.P.Length]; dn = new Vector3[s.P.Length]; }
                        m.GetBlendShapeFrameVertices(idx, m.GetBlendShapeFrameCount(idx) - 1, dv, dn, null);
                        for (int i = 0; i < s.P.Length; i++) { s.P[i] += dv[i] * w; if (s.N != null) s.N[i] += dn[i] * w; }
                        moved = true;
                    }
                    if (moved && s.N != null) for (int i = 0; i < s.N.Length; i++) s.N[i] = s.N[i].normalized;
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

        // Fidelity: one level of subdivision (Subdiv.cs) of the mesh the renderer shows. A MorphState clone (readable, ours) is subdivided in
        // place and keeps being the same Mesh object, so MorphState's swap detection (r.sharedMesh != slot.Clone) is not disturbed. The game's own
        // GPU-only mesh is never touched: it gets a readable clone of ours first (like the non-Fidelity builds, but made here, before the read), and
        // that clone is subdivided. Subdiv.Apply returns null when it skipped the mesh (note says why): then nothing changes for that mesh.
        // A throw from Subdiv is logged and switches subdivision off for the session (the build goes on with Balanced's meshes).
        void Subdivide(SkinnedMeshRenderer r, Rs rs, List<string> problems)
        {
            var m = r.sharedMesh;
            Mesh own = null;
            try
            {
                string note;
                if (IsMorphClone(m))
                {
                    var u = Subdiv.Apply(m, r, out note);
                    if (u != null) { rs.Undo = u; _subdivN++; }
                    Plugin.Log.LogInfo("Soft zones: Fidelity " + (u != null ? "subdivided " : "left alone ") + m.name + (string.IsNullOrEmpty(note) ? "" : ": " + note));
                    return;
                }
                MeshData d;
                if (!_data.TryGetValue(m, out d))
                {
                    string err;
                    d = MeshData.Read(m, out err);
                    if (d == null) { problems.Add(m.name + ": " + err); return; }
                    _data[m] = d;
                }
                own = MorphState.Clone(m, d);
                own.name = m.name + " (SfSoft)";
                _origOf[own] = m;
                r.sharedMesh = own;
                var undo = Subdiv.Apply(own, r, out note);
                if (undo == null)
                {
                    r.sharedMesh = m; _origOf.Remove(own); UnityEngine.Object.Destroy(own);
                    Plugin.Log.LogInfo("Soft zones: Fidelity left " + m.name + " alone" + (string.IsNullOrEmpty(note) ? "" : ": " + note));
                    return;
                }
                rs.Undo = undo; rs.Orig = m; rs.OwnMesh = true; rs.Base = own; rs.Mesh = own; rs.InPlace = true; _subdivN++;
                Plugin.Log.LogInfo("Soft zones: Fidelity subdivided " + m.name + " (own readable clone)" + (string.IsNullOrEmpty(note) ? "" : ": " + note));
            }
            catch (Exception e)
            {
                _subdivFailed = true;
                Plugin.Log.LogError("Soft zones: subdivision threw (" + e.GetType().Name + ": " + e.Message + "); Fidelity falls back to Balanced for this session");
                if (own != null && rs.Undo == null)
                {
                    try { if (r != null && r.sharedMesh == own) r.sharedMesh = m; } catch (Exception) { }
                    _origOf.Remove(own); UnityEngine.Object.Destroy(own);
                }
            }
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
            BuildVersion++;
            GpuRenderers.Clear();
            foreach (var kv in _rs)
            {
                var r = kv.Key; var rs = kv.Value;
                rs.Mask = null;
                rs.Zone = null;
                try
                {
                if (rs.Used && r != null) r.bones = rs.Bones;
                if (rs.Mesh != null && (rs.Used || rs.Undo != null))
                {
                    if (rs.InPlace)
                    {
                        if (rs.OwnMesh) { }   // our own clone: destroyed below, nothing to put back
                        else if (rs.Undo != null)
                        {
                            // Subdivided: Undo.Restore() puts back the mesh exactly as it was when Subdiv.Apply ran (vertices, attributes, weights, bind
                            // poses, triangles, blendshapes), which covers our weight and helper bind-pose changes, so the weights are NOT restored here.
                            // The bind poses are the one thing that may have moved since (MorphState re-derives them when its joint weight changes): keep
                            // the current ones of the mesh's own bones across the Restore.
                            Matrix4x4[] bp = null;
                            try
                            {
                                bp = rs.Mesh.bindposes;
                                if (rs.Used && bp.Length > rs.BaseCount) { var t = new Matrix4x4[rs.BaseCount]; Array.Copy(bp, t, rs.BaseCount); bp = t; }
                                rs.Undo.Restore();
                                if (bp.Length == rs.Mesh.bindposes.Length) rs.Mesh.bindposes = bp;
                            }
                            catch (Exception e) { Plugin.Log.LogWarning("Soft zones: restoring '" + rs.Mesh.name + "' after subdivision: " + e.Message); }
                            rs.Undo = null;
                        }
                        else if (rs.Saved != null)
                        {
                            // only onto the mesh the weights were read from (same vertex count); never leave it half restored
                            if (rs.Saved.Length == rs.Mesh.vertexCount) rs.Mesh.boneWeights = rs.Saved;
                            var bp = rs.Mesh.bindposes;
                            if (rs.BaseCount > 0 && bp.Length > rs.BaseCount) { var t = new Matrix4x4[rs.BaseCount]; Array.Copy(bp, t, rs.BaseCount); rs.Mesh.bindposes = t; }
                        }
                    }
                    else if (rs.Used)
                    {
                        if (r != null && r.sharedMesh == rs.Mesh) r.sharedMesh = rs.Base;
                        _origOf.Remove(rs.Mesh);
                        UnityEngine.Object.Destroy(rs.Mesh);
                    }
                }
                if (rs.OwnMesh && rs.Mesh != null)   // our own clone made for the subdivision
                {
                    if (r != null && r.sharedMesh == rs.Mesh) r.sharedMesh = rs.Orig;
                    _origOf.Remove(rs.Mesh);
                    UnityEngine.Object.Destroy(rs.Mesh);
                }
                }
                catch (Exception e) { Plugin.Log.LogWarning("Soft zones: giving back '" + (r != null ? r.name : "a renderer") + "': " + e.Message); }
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
