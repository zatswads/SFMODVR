using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // A pose sampled once per rendered frame, with its velocity, extrapolated to a fixed step. The controllers and her bones only change
    // per frame, but physics steps several times in one: chasing the stale sample made the hands bang-bang between accelerating and
    // braking (0.7.8 package A uses it for the palms and for her kinematic segments; unused in the interfaces package).
    internal struct Track
    {
        public Vector3 P, V; public Quaternion Q; public float T; public bool Ok;
        public void Sample(Vector3 p, Quaternion q, float now, float frameDt, float maxV)
        {
            if (Ok && frameDt > 1e-4f) { var raw = (p - P) / frameDt; float m2 = raw.sqrMagnitude; if (m2 > maxV * maxV) raw *= maxV / Mathf.Sqrt(m2); V = Vector3.Lerp(V, raw, 0.5f); }
            else V = Vector3.zero;
            P = p; Q = q; T = now; Ok = true;
        }
        public Vector3 At(float t, float maxLead) { return P + V * Mathf.Clamp(t - T, 0f, maxLead); }
        public void Reset() { Ok = false; V = Vector3.zero; }
    }

    // What the integrator hands HerBody every frame (Body fills it from the config).
    internal class HerBodySettings
    {
        public bool On, PushByTouch = true;
        public float Softness = 0.5f;           // 0..1: how much her soft zones give (the inset of the collision shapes)
        public List<SoftCapsule> Caps;           // Body.Contacts(): what can touch her this frame
    }

    // Her body as a set of segments (the pelvis, spine, chest, neck, head, thighs, calves, arms, butt and breast spheres): which bones
    // they follow, how big her skin is around them (read from her body meshes), where they stand now, and how hard hands push them.
    // The geometry is shared: the physics hands build PhysX bodies on it (PhysicsHands.cs), Overlap turns the pushes into spring
    // torques, and a geometric push for hands that aren't physics hands plugs in here (GeoPush). It exists whenever Physics or the
    // physics hands are on, not only with the hands. Everything is built once and rebuilt when she changes (checked every 2 s).
    internal static class HerBody
    {
        public sealed class SegDef
        {
            public string Name, A, B, Owner, AAlt; public bool Soft, AllDesc, Hull; public int Space; /*0 axis A->B, 1 bone space (head)*/ public float Def, Pct;
            // alt: the bone to use when A doesn't exist; pct: which percentile of the vertex distances is the skin radius (0.4 keeps
            // the collider inside the skin; breasts use 0.6 so a hand stops at the visible surface of a round shape);
            // hull / space: convex-hull collider and its frame (0.7.8, unused until the hull package)
            public SegDef(string n, string a, string b, string o, bool soft, float def, bool all = false, string alt = null, float pct = 0.4f, bool hull = false, int space = 0)
            { Name = n; A = a; B = b; Owner = o; Soft = soft; Def = def; AllDesc = all; AAlt = alt; Pct = pct; Hull = hull; Space = space; }
        }

        // Her segment names (what TouchingHer / PushOn take): pelvis, spine, chest, neck, head, thigh_L, thigh_R, calf_L, calf_R,
        // upperarm_L, upperarm_R, forearm_L, forearm_R, butt_L, butt_R, breast_L, breast_R (those whose bones were found).
        public static readonly SegDef[] Defs =
        {
            new SegDef("pelvis", "Hip_L", "Hip_R", "Root_M", true, 0.10f, hull: true),
            new SegDef("spine", "Spine1_M", "Chest_M", "Spine1_M", true, 0.10f, hull: true),
            new SegDef("chest", "Chest_M", "Neck_M", "Chest_M", true, 0.11f, hull: true),
            new SegDef("neck", "Neck_M", "Head_M", "Neck_M", false, 0.05f),
            new SegDef("head", "Head_M", null, "Head_M", false, 0.095f, all: true, hull: true, space: 1),
            new SegDef("thigh_L", "Hip_L", "Knee_L", "Hip_L", true, 0.085f, hull: true),
            new SegDef("thigh_R", "Hip_R", "Knee_R", "Hip_R", true, 0.085f, hull: true),
            new SegDef("calf_L", "Knee_L", "Ankle_L", "Knee_L", false, 0.055f, hull: true),
            new SegDef("calf_R", "Knee_R", "Ankle_R", "Knee_R", false, 0.055f, hull: true),
            new SegDef("upperarm_L", "Shoulder_L", "Elbow_L", "Shoulder_L", false, 0.045f, hull: true),
            new SegDef("upperarm_R", "Shoulder_R", "Elbow_R", "Shoulder_R", false, 0.045f, hull: true),
            new SegDef("forearm_L", "Elbow_L", "Wrist_L", "Elbow_L", false, 0.035f, hull: true),
            new SegDef("forearm_R", "Elbow_R", "Wrist_R", "Elbow_R", false, 0.035f, hull: true),
            new SegDef("butt_L", "Butt_L", null, "Butt_L", true, 0.09f),
            new SegDef("butt_R", "Butt_R", null, "Butt_R", true, 0.09f),
            // 0.7.7: the breasts, which no other segment owned (hands sank to the chest capsule). The sphere follows the DB-driven
            // LowerBreast (UpperBreast is the DynamicBone root and never moves); its vertices are everything skinned below UpperBreast.
            new SegDef("breast_L", "Left_LowerBreast", null, "Left_UpperBreast", true, 0.06f, all: true, alt: "Left_UpperBreast", pct: 0.6f),
            new SegDef("breast_R", "Right_LowerBreast", null, "Right_UpperBreast", true, 0.06f, all: true, alt: "Right_UpperBreast", pct: 0.6f),
        };

        // Defs[i].Name: the segment index everywhere (Overlap's push bits, PushOn(int))
        public static readonly string[] SegNames = MakeNames();
        static string[] MakeNames() { var n = new string[Defs.Length]; for (int i = 0; i < n.Length; i++) n[i] = Defs[i].Name; return n; }

#pragma warning disable 649   // fields the 0.7.8 hull / geometric push / follow packages fill
        public sealed class Seg
        {
            public int Index;                       // into Defs / SegNames
            public SegDef Def;
            public string Name, Owner;
            public Transform A, B;                  // B null: a sphere around LocalCenter (in A's space)
            public bool Soft, Sphere, Hull, Drop;   // Drop: a sphere that found too few vertices to be placed (breast): not kept
            public int Space, RollAxis;
            public Vector3 LocalCenter;
            public float RSkin, RDef, BuildScale = 1f, HullLen, Len0;   // Len0: |B - A| in metres at build (SegPose reports the live length against it)
            public int Verts;
            public Vector3[] HullPts; public int[] HullTris;            // convex hull of the skin (canonical frame, metres); null until the hull package
            // contact impulses (PhysicsHands.NoteContact adds during the physics step, PublishPhys turns them into PhysF at the next fixed step)
            public Vector3 ImpSum, ImpPt; public float ImpW, ImpWL, ImpWR;
            public Vector3 PhysF, PhysPt, GeoF, GeoPt; public float GeoW; public bool PushRight; public CapSource GeoBy; public float GeoDepth;
            public bool PushLogged;
            public float TouchT = -100f;            // Time.fixedTime of the last contact with a hand collider (see PhysicsHands.TouchingHer)
            public Track Trk;                       // her bones' pose sampled per frame (0.7.8 package A)
        }
#pragma warning restore 649

        public static readonly List<Seg> Segs = new List<Seg>();   // built segments (dropped ones excluded), in Defs order
        public static bool Built;
        public static Transform Root;
        public static float Unit = 1f;                              // mesh units -> metres
        public static string Note = "";                             // why nothing is built ("only 2 bones found"), or ""
        public static string Status = "";
        public static int BuildVersion;                             // bumped on every build and release: PhysicsHands rebuilds its bodies when it changes
        // Whether anything can push her limbs now: the physics hands (their contact impulses) or, with the geometric push (0.7.8), other hands.
        public static bool PushActive { get { return Built && (PhysicsHands.Active || _geoActive); } }

        static readonly Seg[] _byIndex = new Seg[Defs.Length];
        static readonly List<SkinnedMeshRenderer> _builtRends = new List<SkinnedMeshRenderer>();
        static readonly HashSet<string> _once = new HashSet<string>();
        static bool _dirty, _geoActive;
        static float _next, _nextCheck, _builtMorph;
        static int _bindVersion;

        static void LogOnce(string key, string msg) { if (_once.Add(key)) Plugin.Log.LogInfo("Her body: " + msg); }
        static bool Finite(Vector3 v) { return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)); }

        // ================= update =================

        // Per frame from Body.ApplyAll (after her bone changes, before Overlap reads the pushes): builds her segments, rebuilds them
        // when she changes (every 2 s: renderers, bones, character shape), runs the geometric push.
        public static void Update(Character her, HerBodySettings s)
        {
            if (s == null || !s.On || her == null || her.Root == null) return;
            float now = Time.unscaledTime;
            if (Built)
            {
                string why = null;
                if (Root != her.Root) why = "her root changed";
                else if (_dirty) why = "a bone is gone";
                else if (now >= _nextCheck)
                {
                    _nextCheck = now + 2f;
                    if (!SameRends(her.Renderers)) why = "her renderers changed";
                    else if (MorphState.BindVersion != _bindVersion) why = "the character's bind poses moved";
                    else if (Mathf.Abs(MorphWeight(her) - _builtMorph) > 0.1f) why = "the character shape's weight changed";
                    else
                        for (int i = 0; i < Segs.Count; i++)
                            if (Segs[i].A == null || (!Segs[i].Sphere && Segs[i].B == null)) { why = "a bone is gone"; break; }
                }
                if (why != null) { Plugin.Log.LogInfo("Her body: rebuilding (" + why + ")"); Rebuild(her, s, now); }
            }
            else if (now >= _next) Rebuild(her, s, now);
            if (Built) GeoPush(s);
        }

        static void Rebuild(Character her, HerBodySettings s, float now)
        {
            try { Build(her, s); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Her body: segments failed: " + e);
                Clear();
                BuildVersion++;
                Note = "her: collider build failed";
                _next = now + 10f;
            }
            _nextCheck = now + 2f;
        }

        // A bone the segments follow was destroyed (PhysicsHands noticed while stepping): rebuild at the next Update.
        public static void MarkDirty() { _dirty = true; }

        static bool SameRends(List<SkinnedMeshRenderer> now)
        {
            if (now.Count != _builtRends.Count) return false;
            for (int i = 0; i < now.Count; i++) if (now[i] != _builtRends[i]) return false;
            return true;
        }

        // Same measure as SoftZones.MorphWeight: the first blendshape of the first body mesh that has any (a character's clone, not the game's mesh).
        static float MorphWeight(Character her)
        {
            foreach (var r in her.Renderers)
                if (r != null && r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0 && r.sharedMesh.name.ToLowerInvariant().Contains("body")) return r.GetBlendShapeWeight(0) / 100f;
            return 0f;
        }

        // Everything given back (physics and soft zones off, she is gone). PhysicsHands notices through BuildVersion.
        public static void Release()
        {
            bool had = Built || Segs.Count > 0 || Status.Length > 0;
            Clear();
            Note = ""; _next = 0f; _nextCheck = 0f;
            if (had) BuildVersion++;
        }

        static void Clear()
        {
            Segs.Clear();
            Array.Clear(_byIndex, 0, _byIndex.Length);
            _builtRends.Clear();
            Built = false; Root = null; Status = ""; _dirty = false; _geoActive = false;
        }

        public static Seg Find(string name)
        {
            if (name == null) return null;
            for (int i = 0; i < Segs.Count; i++) if (string.Equals(Segs[i].Name, name, StringComparison.Ordinal)) return Segs[i];
            return null;
        }

        // ================= building (pure geometry, once per build) =================

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

        // How far the collision shape of a segment is pulled in from her skin: soft zones give by the softness setting (so the soft
        // contact acts first), firm parts barely.
        public static float Inset(Seg g, float softness) { return g.Soft ? Mathf.Lerp(0.02f, 0.045f, Mathf.Clamp01(softness)) : 0.008f; }

        static void Build(Character her, HerBodySettings s)
        {
            Clear();
            Root = her.Root;
            var segs = new List<Seg>();
            for (int di = 0; di < Defs.Length; di++)
            {
                var d = Defs[di];
                var a = FindBone(her, d.A);
                if (a == null && d.AAlt != null)
                {
                    a = FindBone(her, d.AAlt);
                    if (a != null) LogOnce("heralt" + d.Name, "collider " + d.Name + ": " + d.A + " not found, following " + d.AAlt + " instead");
                }
                var b = d.B != null ? FindBone(her, d.B) : null;
                if (a == null || (d.B != null && b == null)) { LogOnce("hermiss" + d.Name, "collider " + d.Name + ": bone " + (a == null ? d.A : d.B) + " not found, skipped"); continue; }
                segs.Add(new Seg { Index = di, Def = d, Name = d.Name, Owner = d.Owner, A = a, B = b, Sphere = d.B == null, Soft = d.Soft, Hull = d.Hull, Space = d.Space, RDef = d.Def, RSkin = d.Def });
            }
            if (segs.Count < 4)
            {
                Note = "her: only " + segs.Count + " bones found"; _next = Time.unscaledTime + 10f; Root = null;
                BuildVersion++;
                return;
            }

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
            Unit = 1f;
            Vector3 pa, pb;
            var hipL = FindBone(her, "Hip_L"); var kneeL = FindBone(her, "Knee_L");
            if (hipL != null && kneeL != null && posOf.TryGetValue(hipL, out pa) && posOf.TryGetValue(kneeL, out pb) && (pa - pb).magnitude > 1e-4f)
            {
                float ratio = (hipL.position - kneeL.position).magnitude / (pa - pb).magnitude;
                if (Mathf.Abs(ratio - 1f) > 0.5f) Unit = ratio;
                Plugin.Log.LogInfo("Her body: skin units: thigh " + (pa - pb).magnitude.ToString("0.###") + " in the bind pose, " + (hipL.position - kneeL.position).magnitude.ToString("0.###") + " m now -> factor " + Unit.ToString("0.###"));
            }
            var segOf = new Dictionary<Transform, int>();
            for (int i = 0; i < segs.Count; i++)
            {
                var owner = FindBone(her, segs[i].Owner);
                if (owner == null) continue;
                var set = new HashSet<Transform>();
                Owned(owner, Stem(owner.name), segs[i].Def.AllDesc, set);
                bool claim = segs[i].Name.StartsWith("breast_", StringComparison.Ordinal);   // the breast bones are the breast's even if another part's stem matched them
                foreach (var t in set) if (claim || !segOf.ContainsKey(t)) segOf[t] = i;
            }
            var pts = new List<Vector3>[segs.Count];
            for (int i = 0; i < pts.Length; i++) pts[i] = new List<Vector3>();
            int bodies = 0;
            foreach (var r in her.Renderers)
            {
                if (r == null || r.sharedMesh == null || !r.sharedMesh.name.ToLowerInvariant().Contains("body")) continue;
                string err;
                var d = MeshData.Read(r.sharedMesh, out err);
                if (d == null || d.Weights == null || d.Pos == null) { Plugin.Log.LogWarning("Her body: skin '" + r.sharedMesh.name + "' unreadable (" + (err ?? "no weights") + ")"); continue; }
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
            var sbLog = new StringBuilder("Her body: segments from " + bodies + " body meshes:");
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
                    // LowerBreast may move with the breast physics yet carry no skin weights (no bind pose): then follow the fallback bone
                    // (UpperBreast, the chain root: placed right, just without the jiggle) instead of dropping the breast collider
                    if (list.Count >= 8 && !bindOfBone.ContainsKey(g.A))
                    {
                        var alt = g.Def.AAlt != null ? FindBone(her, g.Def.AAlt) : null;
                        if (alt != null && bindOfBone.ContainsKey(alt))
                        {
                            LogOnce("herbind" + g.Name, "collider " + g.Name + ": " + g.A.name + " has no bind pose, following " + alt.name + " instead");
                            g.A = alt;
                        }
                    }
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
                    g.RSkin = dists[(int)(g.Def.Pct * (dists.Count - 1))] * Unit;
                }
                else if (g.Sphere && g.Name.StartsWith("breast_", StringComparison.Ordinal)) g.Drop = true;   // no vertices: a sphere at the bone's origin would be a guess
                float inset = Inset(g, s.Softness);
                if (g.Drop) { sbLog.Append("\n  ").Append(g.Name).Append(" (").Append(g.A.name).Append(" sphere): only ").Append(g.Verts).Append(" verts under ").Append(g.Owner).Append(", not made"); continue; }
                sbLog.Append("\n  ").Append(g.Name).Append(" (").Append(g.A.name).Append(g.B != null ? "->" + g.B.name : " sphere" + (g.Owner != g.A.name ? ", owner " + g.Owner : "")).Append("): ")
                     .Append(g.Verts).Append(" verts, skin r ").Append(g.RSkin.ToString("0.000")).Append(dists.Count >= 12 ? "" : " (default)")
                     .Append(", inset ").Append(inset.ToString("0.000")).Append(" -> r ").Append(Mathf.Max(0.012f, g.RSkin - inset).ToString("0.000")).Append(g.Soft ? " [soft zone]" : "");
            }
            Plugin.Log.LogInfo(sbLog.ToString());

            foreach (var g in segs)
            {
                if (g.Drop) continue;
                g.BuildScale = Mathf.Max(1e-4f, Mathf.Abs(g.A.lossyScale.x));
                if (!g.Sphere) g.Len0 = Mathf.Max(1e-4f, (g.B.position - g.A.position).magnitude);
                Segs.Add(g);
                _byIndex[g.Index] = g;
            }
            _builtRends.AddRange(her.Renderers);
            _bindVersion = MorphState.BindVersion;
            _builtMorph = MorphWeight(her);
            Built = true;
            Note = "";
            Status = "Her body: " + Segs.Count + " segments";
            BuildVersion++;
        }

        // ================= live pose =================

        // Where a segment's collision shape stands now (the same placement the capsules always had): axis segments at the middle of
        // A -> B, rotated so +Y runs from A to B; spheres at LocalCenter in A's space. scale = (rr, rl, rr): rr how much her bone was
        // scaled since the build (the skin got fatter or thinner), rl the live length of A -> B against Len0 (1 for spheres).
        // false when a bone is gone or the pose isn't usable. (0.7.8 hull package: the origin moves to A, see design 1.3.)
        public static bool SegPose(Seg g, out Vector3 p, out Quaternion q, out Vector3 scale)
        {
            p = Vector3.zero; q = Quaternion.identity; scale = Vector3.one;
            if (g.A == null || (!g.Sphere && g.B == null)) return false;
            float rr = Mathf.Abs(g.A.lossyScale.x) / g.BuildScale;
            if (g.Sphere) p = g.A.TransformPoint(g.LocalCenter);
            else
            {
                Vector3 a = g.A.position, d = g.B.position - a;
                float len = d.magnitude;
                if (!(len > 1e-4f) || !Finite(d)) return false;
                p = a + d * 0.5f;
                q = Quaternion.FromToRotation(Vector3.up, d / len);
                scale = new Vector3(rr, len / g.Len0, rr);
                return Finite(p);
            }
            scale = new Vector3(rr, 1f, rr);
            return Finite(p);
        }

        // ================= pushes =================

        // The impulse a physics-hand collider gave her segment during the last physics step, along the contact normal oriented to point
        // at the hand (Unity's normal sign convention isn't worth trusting: the hand collider's centre is always on the hand's side of
        // the contact point), and where it acted. Summed over every hand collider touching the segment; PublishPhys turns it into a force.
        public static void AddImpulse(Seg g, bool left, Collision c)
        {
            if (c.contactCount < 1) return;
            float mag = c.impulse.magnitude;
            if (!(mag > 0f) || float.IsInfinity(mag)) return;
            var cp = c.GetContact(0);
            Vector3 n = cp.normal;
            var mine = cp.thisCollider;   // the hand collider (the one in c.collider is hers)
            if (mine == c.collider) mine = cp.otherCollider;   // in case the sides come the other way round
            if (ReferenceEquals(mine, null) || mine == null) return;
            if (Vector3.Dot(n, mine.bounds.center - cp.point) < 0f) n = -n;   // bounds.center: the shape's middle, not the transform's origin
            g.ImpSum += n * mag;
            g.ImpPt += cp.point * mag;
            g.ImpW += mag;
            if (left) g.ImpWL += mag; else g.ImpWR += mag;
        }

        // Fixed step: the impulses summed since the last one -> the force on her over that step (the hand's push is the opposite of the
        // normal that points at the hand), low-passed over 50 ms (contacts chatter from step to step), then the sums restart.
        public static void PublishPhys(Seg g, float fixedDt)
        {
            Vector3 raw = Vector3.zero;
            if (g.ImpW > 1e-6f && fixedDt > 0f)
            {
                Vector3 f = -g.ImpSum / fixedDt;
                if (Finite(f))
                {
                    raw = f; g.PhysPt = g.ImpPt / g.ImpW; g.PushRight = g.ImpWR > g.ImpWL;
                    if (!g.PushLogged && f.sqrMagnitude >= 0.2f * 0.2f)
                    {
                        g.PushLogged = true;
                        Plugin.Log.LogInfo("Her body: first push on " + g.Name + " by your " + (g.PushRight ? "right" : "left") + " physics hand (" + f.magnitude.ToString("0.0") + " N)");
                    }
                }
            }
            g.PhysF = Vector3.Lerp(g.PhysF, raw, 1f - Mathf.Exp(-fixedDt / 0.05f));
            g.ImpSum = g.ImpPt = Vector3.zero; g.ImpW = g.ImpWL = g.ImpWR = 0f;
        }

        // The physics hands' bodies are gone (hands off, collisions off): their last push must not linger.
        public static void ClearPhys()
        {
            for (int i = 0; i < Segs.Count; i++)
            {
                var g = Segs[i];
                g.PhysF = Vector3.zero; g.ImpSum = g.ImpPt = Vector3.zero; g.ImpW = g.ImpWL = g.ImpWR = 0f;
            }
        }

        // Pushes from hands that aren't physics hands (your hand models, the partner's hands), by how deep they are in her. 0.7.8 geometric
        // push package: fills GeoF / GeoPt / GeoBy per segment here and sets _geoActive. Nothing yet.
        static void GeoPush(HerBodySettings s)
        {
        }

        // The push on one of her segments over the last step: the force ON HER (N, world), where it acted, and who pushed ("your right
        // physics hand", "Player", ...). The physics hands' contact force (PhysF) and the geometric push (GeoF) add up; false below 0.2 N.
        public static bool PushOn(string seg, out Vector3 point, out Vector3 forceOnHer, out string by)
        {
            return PushOn(Find(seg), out point, out forceOnHer, out by);
        }

        public static bool PushOn(int segIndex, out Vector3 point, out Vector3 forceOnHer, out string by)
        {
            return PushOn(segIndex >= 0 && segIndex < _byIndex.Length ? _byIndex[segIndex] : null, out point, out forceOnHer, out by);
        }

        static bool PushOn(Seg g, out Vector3 point, out Vector3 forceOnHer, out string by)
        {
            point = Vector3.zero; forceOnHer = Vector3.zero; by = null;
            if (g == null || !PushActive) return false;
            Vector3 f = g.PhysF + g.GeoF;
            if (f.sqrMagnitude < 0.2f * 0.2f) return false;
            float wp = g.PhysF.magnitude, wg = g.GeoF.magnitude;
            point = (g.PhysPt * wp + g.GeoPt * wg) / (wp + wg);
            forceOnHer = f;
            by = wp >= wg ? (g.PushRight ? "your right physics hand" : "your left physics hand") : (g.GeoBy != null ? g.GeoBy.Name : "a hand");
            return true;
        }
    }
}
