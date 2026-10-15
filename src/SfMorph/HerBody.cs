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
        public Vector3 P, V, W; public Quaternion Q; public float T; public bool Ok;   // W: angular velocity (rad/s, world axis)
        public void Sample(Vector3 p, Quaternion q, float now, float frameDt, float maxV)
        {
            if (Ok && frameDt > 1e-4f)
            {
                var raw = (p - P) / frameDt; float m2 = raw.sqrMagnitude; if (m2 > maxV * maxV) raw *= maxV / Mathf.Sqrt(m2); V = Vector3.Lerp(V, raw, 0.5f);
                // angular velocity from the shortest rotation Q -> q (world axis), low-passed like V and clamped
                Quaternion dq = q * Quaternion.Inverse(Q);
                if (dq.w < 0f) { dq.x = -dq.x; dq.y = -dq.y; dq.z = -dq.z; dq.w = -dq.w; }
                float ang; Vector3 ax;
                dq.ToAngleAxis(out ang, out ax);
                if (ang > 180f) ang -= 360f;
                Vector3 w = float.IsNaN(ax.x) || float.IsInfinity(ax.x) || ang < 1e-3f ? Vector3.zero : ax * (ang * Mathf.Deg2Rad / frameDt);
                float wm = w.magnitude; if (wm > MaxW) w *= MaxW / wm;
                W = Vector3.Lerp(W, w, 0.5f);
            }
            else { V = Vector3.zero; W = Vector3.zero; }
            P = p; Q = q; T = now; Ok = true;
        }
        public const float MaxW = 20f;   // rad/s
        public Vector3 At(float t, float maxLead) { return P + V * Mathf.Clamp(t - T, 0f, maxLead); }
        // the sampled rotation turned on at W for the same lead (the target of the hands' rotation follow)
        public Quaternion QAt(float t, float maxLead)
        {
            float lead = Mathf.Clamp(t - T, 0f, maxLead), a = W.magnitude * lead;
            return a > 1e-5f ? Quaternion.AngleAxis(a * Mathf.Rad2Deg, W / W.magnitude) * Q : Q;
        }
        public void Reset() { Ok = false; V = Vector3.zero; W = Vector3.zero; }
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
            public Transform RollRef;               // the bone whose axes give the hull its roll and whose scale is rr: A, or the Owner bone when B is not below A (the pelvis: Hip_L/Hip_R are siblings, Root_M is the pelvis)
            public bool Soft, Sphere, Hull, Drop;   // Drop: a sphere that found too few vertices to be placed (breast): not kept
            public int Space, RollAxis;
            public Vector3 LocalCenter;
            public float RSkin, RDef, BuildScale = 1f, HullLen, Len0;   // Len0: |B - A| in metres at build (SegPose reports the live length against it)
            public int Verts;
            public Vector3[] HullPts; public int[] HullTris;            // convex hull of the skin (canonical frame, metres); null until the hull package
            // contact impulses (PhysicsHands.NoteContact adds during the physics step, PublishPhys turns them into PhysF at the next fixed step)
            public Vector3 ImpSum, ImpPt; public float ImpW, ImpWL, ImpWR;
            public Vector3 PhysF, PhysPt, GeoF, GeoPt; public float GeoW; public bool PushRight; public CapSource GeoBy; public float GeoDepth;
            public string GeoName;                  // who pushed geometrically when the capsule has no CapSource (its Owner), never built per frame
            public bool PushLogged;
            // Space 1 (the head): where the shape's centre is in the segment-local frame (metres at scale 1); a fallback sphere collider
            // sits there (SegPose puts the frame at A, not at the sphere's centre). Zero for every other segment.
            public Vector3 FrameCenter;
            public float HullRMin, HullRMax;        // radial extent of the hull (about the axis, or about the centre): for logs and markers
            public float TouchT = -100f;            // Time.fixedTime of the last contact with a hand collider (see PhysicsHands.TouchingHer)
            public Track Trk;                       // her bones' pose sampled per frame (0.7.8 package A)
            // 0.7.9 reactions: the last physics-hand contact ENTER that was a new touch (no contact for 0.15 s before it)
            public float HitT = -100f, HitVn, HitSpeed;   // Time.fixedTime; |normal component| and magnitude of Collision.relativeVelocity (m/s)
            public Vector3 HitPt, HitDir;                 // contact point; direction INTO her (the opposite of AddImpulse's hand-facing normal)
            public bool HitLeft;
            public float GeoVn;                           // max approach speed (m/s) of any geometric capsule on this segment this frame (0 when none)
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

        // hull grids (design 1.2): axis segments NAlong x NAround + 2 poles = 194 points, bone-space ones NLat x NLon + 2 = 130 (PhysX convex
        // hulls take at most 255 vertices)
        const int NAlong = 12, NAround = 16, NLat = 8, NLon = 16;
        internal const int HullCols = NAround;   // both grids are row-major with this many columns (rows along Y / from south to north), then pole0 (below row 0), pole1 (above the last row)
        // geometric push (3.2)
        // 300 N/m: 3 cm deep gives 9 N (~14 deg at the elbow); 150 gave 4.5 N, barely visible. The skin gives SkinGive first (the soft zones dent).
        const float SkinGive = 0.015f;
        const float PushStiffness = 300f, PushDamp = 4f, PushCapPerHand = 20f, PushCapPerSeg = 40f, PushMinN = 0.2f;
        const float MaxCapSpeed = 3f, TeleportSpeed = 8f;   // m/s, the same rules as SoftSim

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
                            if (Segs[i].A == null || Segs[i].RollRef == null || (!Segs[i].Sphere && Segs[i].B == null)) { why = "a bone is gone"; break; }
                }
                if (why != null) { Plugin.Log.LogInfo("Her body: rebuilding (" + why + ")"); Rebuild(her, s, now); }
            }
            else if (now >= _next) Rebuild(her, s, now);
            if (Built)
            {
                GeoPush(s);
                if (!PushActive) ClearPush();
                Tally(now);
            }
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

        // Update threw after a successful build (GeoPush / Tally): give everything back and don't rebuild for 10 s, so a persistent
        // failure doesn't rebuild every frame (build, throw, release, build ...).
        public static void Fail()
        {
            Release();
            _next = Time.unscaledTime + 10f;
            Note = "her: update failed, retrying in 10 s";
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
            _pN = -1; _catN = -1; _pushBits = 0; _tallyAt = 0f; _statBase = "";
            Array.Clear(_sumF, 0, _sumF.Length); Array.Clear(_nF, 0, _nF.Length);
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
                // roll reference: A, unless B is not a descendant of A (hip to hip), then the owner bone (Root_M); that one doesn't flex with a thigh
                Transform rollRef = a;
                if (b != null && !b.IsChildOf(a))
                {
                    var o = FindBone(her, d.Owner);
                    if (o != null) rollRef = o;
                }
                segs.Add(new Seg { Index = di, Def = d, Name = d.Name, Owner = d.Owner, A = a, B = b, RollRef = rollRef, Sphere = d.B == null, Soft = d.Soft, Hull = d.Hull, Space = d.Space, RDef = d.Def, RSkin = d.Def });
            }
            if (segs.Count < 4)
            {
                Note = "her: only " + segs.Count + " bones found"; _next = Time.unscaledTime + 10f; Root = null;
                BuildVersion++;
                return;
            }

            // hull segments: the frame their points live in (the same one SegPose reports at run time, taken from her bones NOW, so the
            // hull is in the segment-local frame at scale 1 in world metres by construction). The scale and length must be known first.
            var fInv = new Matrix4x4[segs.Count];
            var locs = new List<Vector3>[segs.Count];          // hull segments: the skin vertices of the segment in that frame
            var samples = new List<VSample>[segs.Count];       // a few of them kept with their skin weights for the frame check
            for (int i = 0; i < segs.Count; i++)
            {
                var g = segs[i];
                if (!g.Hull) continue;
                g.BuildScale = Mathf.Max(1e-4f, Mathf.Abs(g.RollRef.lossyScale.x));
                Vector3 fp; Quaternion fq;
                if (g.Space == 1) { fp = g.A.position; fq = g.A.rotation; if (!Finite(fp)) fp = Vector3.zero; }
                else
                {
                    g.Len0 = Mathf.Max(1e-4f, (g.B.position - g.A.position).magnitude);
                    ChooseRoll(g);
                    float ln;
                    if (!AxisPose(g, out fp, out fq, out ln)) { g.Hull = false; LogOnce("herframe" + g.Name, "hull " + g.Name + ": its axis is degenerate, capsule only"); continue; }
                }
                g.HullLen = g.Len0;
                fInv[i] = Matrix4x4.TRS(fp, fq, Vector3.one).inverse;
                locs[i] = new List<Vector3>(); samples[i] = new List<VSample>();
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
            int bodies = 0, morphed = 0;
            Vector3[] dv = null;
            foreach (var r in her.Renderers)
            {
                if (r == null || r.sharedMesh == null || !r.sharedMesh.name.ToLowerInvariant().Contains("body")) continue;
                string err;
                var d = MeshData.Read(r.sharedMesh, out err);
                if (d == null || d.Weights == null || d.Pos == null) { Plugin.Log.LogWarning("Her body: skin '" + r.sharedMesh.name + "' unreadable (" + (err ?? "no weights") + ")"); continue; }
                morphed += ApplyMorphs(r, d.Pos, ref dv);   // radii and hull points follow the character's shape
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
                // this mesh's bind space -> each hull segment's frame, rigidly through the segment's own bone A (bind pose of A in THIS mesh,
                // A's matrix now, the frame's inverse): pose independent, includes the units and her scale, no sign or handedness guesses
                var rig = new Matrix4x4[segs.Count]; var rigOk = new bool[segs.Count];
                for (int i = 0; i < segs.Count; i++)
                {
                    if (locs[i] == null) continue;
                    int ia = Array.IndexOf(bs, segs[i].A);
                    if (ia >= 0 && ia < d.Bind.Length) { rig[i] = fInv[i] * segs[i].A.localToWorldMatrix * d.Bind[ia]; rigOk[i] = true; }
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
                    for (int i = 0; i < acc.Length; i++)
                        if (acc[i] >= 0.5f)
                        {
                            Vector3 vp = d.Pos[v];
                            pts[i].Add(vp);
                            if (locs[i] != null)
                            {
                                Matrix4x4 m = rig[i];
                                bool ok = rigOk[i] || FallbackMatrix(w, segOfIdx, i, bs, d.Bind, fInv[i], out m);   // this mesh has no bone A: the segment's heaviest bone
                                if (ok)
                                {
                                    locs[i].Add(m.MultiplyPoint3x4(vp));
                                    if ((locs[i].Count & 7) == 1 && samples[i].Count < 160) samples[i].Add(new VSample { Pos = vp, W = w, Bones = bs, Bind = d.Bind });
                                }
                            }
                            break;
                        }
                }
            }
            var sbLog = new StringBuilder("Her body: segments from " + bodies + " body meshes" + (morphed > 0 ? " (character '" + her.Key + "' " + (MorphWeight(her) * 100f).ToString("0") + " %)" : "") + ":");
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
                if (g.Hull && !g.Drop) BuildHull(g, locs[i]);
                float inset = Inset(g, s.Softness);
                if (g.Drop) { sbLog.Append("\n  ").Append(g.Name).Append(" (").Append(g.A.name).Append(" sphere): only ").Append(g.Verts).Append(" verts under ").Append(g.Owner).Append(", not made"); continue; }
                sbLog.Append("\n  ").Append(g.Name).Append(" (").Append(g.A.name).Append(g.B != null ? "->" + g.B.name : " sphere" + (g.Owner != g.A.name ? ", owner " + g.Owner : "")).Append("): ")
                     .Append(g.Verts).Append(" verts, skin r ").Append(g.RSkin.ToString("0.000")).Append(dists.Count >= 12 ? "" : " (default)");
                if (g.HullPts != null) sbLog.Append(", hull ").Append(g.HullPts.Length).Append(" pts (r ").Append(g.HullRMin.ToString("0.000")).Append("..").Append(g.HullRMax.ToString("0.000")).Append(")");
                sbLog.Append(", inset ").Append(inset.ToString("0.000"));
                if (g.HullPts == null) sbLog.Append(" -> capsule r ").Append(Mathf.Max(0.012f, g.RSkin - inset).ToString("0.000"));
                if (g.Soft) sbLog.Append(" [soft zone]");
            }
            Plugin.Log.LogInfo(sbLog.ToString());

            foreach (var g in segs)
            {
                if (g.Drop) continue;
                g.BuildScale = Mathf.Max(1e-4f, Mathf.Abs(g.RollRef.lossyScale.x));
                if (!g.Sphere) { g.Len0 = Mathf.Max(1e-4f, (g.B.position - g.A.position).magnitude); g.HullLen = g.Len0; }
                Segs.Add(g);
                _byIndex[g.Index] = g;
            }
            // How the hull frame was verified (once per build, cheap): each hull is mapped to the world exactly as the colliders will place it
            // (SegPose), then 64 vertices are skinned with the LIVE bone matrices (all four weights, not the rigid bone-A image the hull was
            // built from). The hull sits on the outer skin, so the nearest hull point is a grid spacing or a concavity away (a few cm); a
            // wrong roll, handedness or units shows as the hull far from the skin (avg > 5 cm) and logs a warning.
            var sbChk = new StringBuilder("Her body: hull frame check (64 live-skinned vertices vs the hull's world points):");
            bool chkBad = false;
            for (int i = 0; i < segs.Count; i++)
                if (!segs[i].Drop && segs[i].HullPts != null) chkBad |= FrameCheck(segs[i], samples[i], sbChk);
            if (chkBad) Plugin.Log.LogWarning(sbChk.ToString()); else Plugin.Log.LogInfo(sbChk.ToString());
            _builtRends.AddRange(her.Renderers);
            _bindVersion = MorphState.BindVersion;
            _builtMorph = MorphWeight(her);
            Built = true;
            Note = "";
            int nh = 0;
            foreach (var g in Segs) if (g.HullPts != null) nh++;
            _statBase = "Her body: " + Segs.Count + " segments (" + nh + " hulls, " + (Segs.Count - nh) + " capsules/spheres)";
            _pushBits = 0;
            Status = _statBase;
            BuildVersion++;
        }

        // ================= hull points (design 1.2) =================

        // One skinned vertex kept from the build to check the hull against the live skin.
        struct VSample { public Vector3 Pos; public BoneWeight W; public Transform[] Bones; public Matrix4x4[] Bind; }

        // Adds the blendshape frames her readable meshes carry to bind-space positions (a character's clone has "SfMorph body/face"
        // shapes whose weights the sliders drive), so the radii and the hull follow her current shape. The last frame of each shape is
        // taken (the clones have one, at weight 100). Returns how many shapes contributed. dv: one scratch array per build.
        static int ApplyMorphs(SkinnedMeshRenderer r, Vector3[] pos, ref Vector3[] dv)
        {
            var m = r.sharedMesh;
            if (m == null || pos == null || !m.isReadable) return 0;   // the game's own GPU-only meshes have no blendshapes
            int bc = m.blendShapeCount, n = m.vertexCount, used = 0;
            if (bc == 0 || pos.Length != n) return 0;
            for (int k = 0; k < bc; k++)
            {
                float wgt = r.GetBlendShapeWeight(k);
                if (Mathf.Abs(wgt) < 0.1f) continue;      // 0.001 of the full weight
                int last = m.GetBlendShapeFrameCount(k) - 1;
                if (last < 0) continue;
                float fw = m.GetBlendShapeFrameWeight(k, last);
                if (!(fw > 0f)) fw = 100f;
                float f = wgt / fw;
                if (dv == null || dv.Length != n) dv = new Vector3[n];
                try { m.GetBlendShapeFrameVertices(k, last, dv, null, null); }
                catch (Exception e) { LogOnce("hermorph" + m.name, "blendshape '" + m.GetBlendShapeName(k) + "' of '" + m.name + "' not read (" + e.Message + "), hull and radii ignore it"); continue; }
                for (int v = 0; v < n; v++) pos[v] += dv[v] * f;
                used++;
            }
            return used;
        }

        // A vertex whose mesh doesn't carry the segment's bone A: take the heaviest of its bones that belongs to the segment.
        static bool FallbackMatrix(BoneWeight w, int[] segOfIdx, int seg, Transform[] bs, Matrix4x4[] bind, Matrix4x4 frameInv, out Matrix4x4 m)
        {
            m = Matrix4x4.identity;
            int best = -1; float bw = 0f;
            Heaviest(w.boneIndex0, w.weight0, segOfIdx, seg, ref best, ref bw);
            Heaviest(w.boneIndex1, w.weight1, segOfIdx, seg, ref best, ref bw);
            Heaviest(w.boneIndex2, w.weight2, segOfIdx, seg, ref best, ref bw);
            Heaviest(w.boneIndex3, w.weight3, segOfIdx, seg, ref best, ref bw);
            if (best < 0 || best >= bs.Length || best >= bind.Length || bs[best] == null) return false;
            m = frameInv * bs[best].localToWorldMatrix * bind[best];
            return true;
        }

        static void Heaviest(int bi, float wt, int[] segOfIdx, int seg, ref int best, ref float bw)
        {
            if (bi < 0 || bi >= segOfIdx.Length || segOfIdx[bi] != seg || !(wt > bw)) return;
            best = bi; bw = wt;
        }

        // The hull of one segment from its skin vertices in the segment-local frame (metres, scale 1). Fills HullPts / HullTris /
        // HullRMin / HullRMax / FrameCenter; leaves HullPts null (capsule only) when there are too few vertices.
        static void BuildHull(Seg g, List<Vector3> loc)
        {
            if (g.Space == 1 && loc != null && loc.Count > 0)
            {
                Vector3 c = Vector3.zero;
                for (int k = 0; k < loc.Count; k++) c += loc[k];
                g.FrameCenter = c / loc.Count;
            }
            g.HullPts = null; g.HullTris = null;
            if (loc == null || loc.Count < 24) { g.Hull = false; LogOnce("hershort" + g.Name, "hull " + g.Name + ": only " + (loc == null ? 0 : loc.Count) + " skin vertices, capsule only"); return; }
            if (g.Space == 1) BuildBoneHull(g, loc); else BuildAxisHull(g, loc);
            if (g.HullPts != null)
                for (int k = 0; k < g.HullPts.Length; k++)
                    if (!Finite(g.HullPts[k])) { g.HullPts = null; g.HullTris = null; g.Hull = false; LogOnce("herhullnan" + g.Name, "hull " + g.Name + ": non-finite points, capsule only"); return; }
        }

        // Axis segments: NAlong bins along A->B x NAround angular bins about it; per cell the vertex farthest from the axis (the outer skin; the
        // inset at cooking time is what lets the skin give first), an empty cell takes RSkin so the hull is never thinner than the capsule it
        // replaces. Range along the axis: the joints +-5 %, widened to the vertices (the pelvis axis runs hip to hip, the torso reaches far
        // beyond it) but never beyond 0.8 x the length past either end; vertices outside that are dropped. Two poles close the ends.
        static void BuildAxisHull(Seg g, List<Vector3> loc)
        {
            float L = g.Len0, lo = -0.8f * L, hi = 1.8f * L;
            float yMin = float.MaxValue, yMax = float.MinValue;
            for (int k = 0; k < loc.Count; k++)
            {
                float y = loc[k].y;
                if (y < lo || y > hi) continue;
                if (y < yMin) yMin = y;
                if (y > yMax) yMax = y;
            }
            float yLo = Mathf.Min(-0.05f * L, yMin), yHi = Mathf.Max(1.05f * L, yMax);
            float dy = (yHi - yLo) / NAlong;
            var cell = new float[NAlong * NAround];
            for (int k = 0; k < cell.Length; k++) cell[k] = -1f;
            float angToBin = NAround / (2f * Mathf.PI);
            for (int k = 0; k < loc.Count; k++)
            {
                Vector3 v = loc[k];
                if (v.y < lo || v.y > hi) continue;
                int i = Mathf.Clamp((int)((v.y - yLo) / dy), 0, NAlong - 1);
                int j = (int)((Mathf.Atan2(v.z, v.x) + Mathf.PI) * angToBin) % NAround;
                float rad = Mathf.Sqrt(v.x * v.x + v.z * v.z);
                int c = i * NAround + j;
                if (rad > cell[c]) cell[c] = rad;
            }
            var pts = new Vector3[NAlong * NAround + 2];
            float rMin = float.MaxValue, rMax = 0f;
            for (int i = 0; i < NAlong; i++)
                for (int j = 0; j < NAround; j++)
                {
                    float rad = cell[i * NAround + j];
                    rad = Mathf.Max(0.006f, rad > 0f ? rad : g.RSkin);
                    float phi = -Mathf.PI + (j + 0.5f) * (2f * Mathf.PI / NAround);
                    pts[i * NAround + j] = new Vector3(rad * Mathf.Cos(phi), yLo + (i + 0.5f) * dy, rad * Mathf.Sin(phi));
                    if (rad < rMin) rMin = rad;
                    if (rad > rMax) rMax = rad;
                }
            float cap = 0.5f * Mathf.Max(0.01f, g.RSkin);
            pts[NAlong * NAround] = new Vector3(0f, yLo - cap, 0f);
            pts[NAlong * NAround + 1] = new Vector3(0f, yHi + cap, 0f);
            g.HullPts = pts; g.HullTris = MakeTris(NAlong, NAround); g.HullRMin = rMin; g.HullRMax = rMax;
        }

        // Bone-space segments (the head): NLat x NLon directions around the centroid, farthest vertex per cell, empty = RSkin, poles on +-Y.
        // Row 0 is the south (-Y) side so the triangle winding is the same as the axis hull's (outward).
        static void BuildBoneHull(Seg g, List<Vector3> loc)
        {
            Vector3 c = g.FrameCenter;
            var cell = new float[NLat * NLon];
            for (int k = 0; k < cell.Length; k++) cell[k] = -1f;
            float angToBin = NLon / (2f * Mathf.PI);
            for (int k = 0; k < loc.Count; k++)
            {
                Vector3 d = loc[k] - c;
                float rad = d.magnitude;
                if (!(rad > 1e-4f)) continue;
                int i = Mathf.Clamp((int)((Mathf.PI - Mathf.Acos(Mathf.Clamp(d.y / rad, -1f, 1f))) * (NLat / Mathf.PI)), 0, NLat - 1);
                int j = (int)((Mathf.Atan2(d.z, d.x) + Mathf.PI) * angToBin) % NLon;
                if (rad > cell[i * NLon + j]) cell[i * NLon + j] = rad;
            }
            var pts = new Vector3[NLat * NLon + 2];
            float rMin = float.MaxValue, rMax = 0f, south = 0f, north = 0f;
            for (int i = 0; i < NLat; i++)
            {
                float theta = Mathf.PI - (i + 0.5f) * (Mathf.PI / NLat);
                float st = Mathf.Sin(theta), ct = Mathf.Cos(theta);
                for (int j = 0; j < NLon; j++)
                {
                    float rad = cell[i * NLon + j];
                    rad = Mathf.Max(0.006f, rad > 0f ? rad : g.RSkin);
                    float phi = -Mathf.PI + (j + 0.5f) * (2f * Mathf.PI / NLon);
                    pts[i * NLon + j] = c + new Vector3(rad * st * Mathf.Cos(phi), rad * ct, rad * st * Mathf.Sin(phi));
                    if (rad < rMin) rMin = rad;
                    if (rad > rMax) rMax = rad;
                    if (i == 0) south += rad / NLon;
                    if (i == NLat - 1) north += rad / NLon;
                }
            }
            pts[NLat * NLon] = c - new Vector3(0f, south, 0f);
            pts[NLat * NLon + 1] = c + new Vector3(0f, north, 0f);
            g.HullPts = pts; g.HullTris = MakeTris(NLat, NLon); g.HullRMin = rMin; g.HullRMax = rMax;
        }

        // The cylinder/sphere grid (rows x cols points, row-major, rows run in +Y) with a fan at each end: pole0 = index rows*cols (below row 0),
        // pole1 = the next one. Unity's winding (cross(b - a, c - a) points out): outward for the canonical frame (checked numerically). 384 / 256 triangles.
        internal static int[] MakeTris(int rows, int cols)
        {
            var t = new int[(rows - 1) * cols * 6 + 2 * cols * 3];
            int n = 0, pole0 = rows * cols, pole1 = pole0 + 1;
            for (int i = 0; i < rows - 1; i++)
                for (int j = 0; j < cols; j++)
                {
                    int j1 = (j + 1) % cols;
                    int a = i * cols + j, b = (i + 1) * cols + j, c = i * cols + j1, d = (i + 1) * cols + j1;
                    t[n++] = a; t[n++] = b; t[n++] = c;
                    t[n++] = b; t[n++] = d; t[n++] = c;
                }
            for (int j = 0; j < cols; j++) { int j1 = (j + 1) % cols; t[n++] = pole0; t[n++] = j; t[n++] = j1; }
            for (int j = 0; j < cols; j++) { int j1 = (j + 1) % cols; t[n++] = pole1; t[n++] = (rows - 1) * cols + j1; t[n++] = (rows - 1) * cols + j; }
            return t;
        }

        // The vertex skinned with the live bone matrices (what the renderer draws, all four weights).
        static bool SkinNow(VSample s, out Vector3 world)
        {
            world = Vector3.zero;
            float ws = 0f;
            Skin1(s, s.W.boneIndex0, s.W.weight0, ref world, ref ws);
            Skin1(s, s.W.boneIndex1, s.W.weight1, ref world, ref ws);
            Skin1(s, s.W.boneIndex2, s.W.weight2, ref world, ref ws);
            Skin1(s, s.W.boneIndex3, s.W.weight3, ref world, ref ws);
            if (!(ws > 0.01f)) return false;
            world /= ws;
            return Finite(world);
        }

        static void Skin1(VSample s, int bi, float wt, ref Vector3 sum, ref float ws)
        {
            if (!(wt > 0f) || bi < 0 || bi >= s.Bones.Length || bi >= s.Bind.Length || s.Bones[bi] == null) return;
            sum += wt * (s.Bones[bi].localToWorldMatrix * s.Bind[bi]).MultiplyPoint3x4(s.Pos);
            ws += wt;
        }

        // Once per build: the hull as the colliders place it (SegPose) against live-skinned vertices. Appends one line to sb; true = looks wrong.
        static bool FrameCheck(Seg g, List<VSample> samples, StringBuilder sb)
        {
            Vector3 p, sc; Quaternion q;
            sb.Append("\n  ").Append(g.Name).Append(": ");
            if (!SegPose(g, out p, out q, out sc) || samples == null || samples.Count == 0) { sb.Append("no pose or no samples"); return false; }
            int n = g.HullPts.Length, grid = n - 2;
            var w = new Vector3[n];
            for (int k = 0; k < n; k++) w[k] = p + q * Vector3.Scale(g.HullPts[k], sc);
            // the hull's radial extent in the world about the live axis (or the head's centre): equals the canonical radii if SegPose's frame is right
            Vector3 centre = g.Space == 1 ? p + q * Vector3.Scale(g.FrameCenter, sc) : p;
            Vector3 ay = g.Space == 1 ? Vector3.zero : q * Vector3.up;
            float rMin = float.MaxValue, rMax = 0f;
            for (int k = 0; k < grid; k++)
            {
                Vector3 v = w[k] - centre;
                v -= ay * Vector3.Dot(v, ay);
                float r = v.magnitude;
                if (r < rMin) rMin = r;
                if (r > rMax) rMax = r;
            }
            int cnt = Mathf.Min(64, samples.Count), used = 0;
            float sum = 0f, max = 0f;
            for (int t = 0; t < cnt; t++)
            {
                Vector3 sp;
                if (!SkinNow(samples[cnt > 1 ? t * (samples.Count - 1) / (cnt - 1) : 0], out sp)) continue;
                float best = float.MaxValue;
                for (int k = 0; k < n; k++) { float d2 = (w[k] - sp).sqrMagnitude; if (d2 < best) best = d2; }
                best = Mathf.Sqrt(best);
                sum += best; if (best > max) max = best; used++;
            }
            if (used == 0) { sb.Append("no skinned samples"); return false; }
            float avg = sum / used;
            bool bad = avg > 0.05f || max > 0.15f || (rMax > 0f && Mathf.Abs(rMax - g.HullRMax) > 0.005f);
            sb.Append("hull r ").Append(rMin.ToString("0.000")).Append("..").Append(rMax.ToString("0.000")).Append(" in the world (skin r ").Append(g.RSkin.ToString("0.000"))
              .Append("), ").Append(used).Append(" skinned verts: nearest hull point avg ").Append((avg * 100f).ToString("0.0")).Append(" cm, max ").Append((max * 100f).ToString("0.0")).Append(" cm");
            if (bad) sb.Append("  <-- frame looks wrong");
            return bad;
        }

        // ================= live pose =================

        // How much her bone (RollRef) is scaled since the build: the skin got fatter or thinner by that much (shared with PhysicsHands' cook).
        public static float RollScale(Seg g) { return Mathf.Abs((g.RollRef != null ? g.RollRef : g.A).lossyScale.x) / g.BuildScale; }

        // The roll about the axis follows RollRef's local X (its Z when X lies along the axis): remembered at build so it never switches at run time.
        static void ChooseRoll(Seg g)
        {
            g.RollAxis = 0;
            Vector3 d = g.B.position - g.A.position;
            if (!(d.sqrMagnitude > 1e-8f)) return;
            Vector3 y = d.normalized, x = g.RollRef.right;
            if ((x - y * Vector3.Dot(x, y)).magnitude < 0.1f) g.RollAxis = 2;
        }

        static Vector3 Perp(Vector3 v, Vector3 y)
        {
            Vector3 x = v - y * Vector3.Dot(v, y);
            float m = x.magnitude;
            return m > 1e-4f ? x / m : Vector3.zero;
        }

        // The axis segments' frame (origin at joint A, Y along A -> B, X from RollRef's local X or Z): the one place both the build and SegPose use.
        static bool AxisPose(Seg g, out Vector3 a, out Quaternion q, out float len)
        {
            q = Quaternion.identity;
            a = g.A.position;
            Vector3 d = g.B.position - a;
            len = d.magnitude;
            if (!(len > 1e-4f) || !Finite(d) || !Finite(a)) return false;
            Vector3 y = d / len;
            bool z = g.RollAxis == 2;
            Vector3 x = Perp(z ? g.RollRef.forward : g.RollRef.right, y);
            if (!(x.sqrMagnitude > 0.5f)) x = Perp(z ? g.RollRef.right : g.RollRef.forward, y);   // the roll axis lies along the segment right now: the other one
            if (!(x.sqrMagnitude > 0.5f)) return false;
            q = Quaternion.LookRotation(Vector3.Cross(x, y), y);   // right = x, up = y, forward = x cross y
            return true;
        }

        // Where a segment's collision shape stands now, in the frame its HullPts are in (design section 11), false when a bone is gone or the
        // pose isn't usable. scale = (rr, rl, rr): rr how much her bone was scaled since the build (the skin got fatter or thinner), rl the live
        // length of A -> B against Len0 (Length sliders stretch along Y only).
        //   axis (Space 0): p = A.position, Y = A -> B, X from RollRef's local X (RollAxis 2: Z; RollRef = A, or the owner bone for the pelvis), scale (rr, rl, rr)
        //   sphere: p = A.TransformPoint(LocalCenter), q = A.rotation, scale (rr, rr, rr) / one
        //   bone space (Space 1, the head): p = A.position, q = A.rotation, scale (rr, rr, rr); the shape's centre is FrameCenter in that frame
        public static bool SegPose(Seg g, out Vector3 p, out Quaternion q, out Vector3 scale)
        {
            p = Vector3.zero; q = Quaternion.identity; scale = Vector3.one;
            if (g.A == null || g.RollRef == null || (!g.Sphere && g.B == null)) return false;
            float rr = RollScale(g);
            if (g.Space == 1)
            {
                p = g.A.position; q = g.A.rotation;
                scale = new Vector3(rr, rr, rr);
                return Finite(p);
            }
            if (g.Sphere)
            {
                p = g.A.TransformPoint(g.LocalCenter); q = g.A.rotation;
                scale = new Vector3(rr, rr, rr);
                return Finite(p);
            }
            float len;
            if (!AxisPose(g, out p, out q, out len)) return false;
            scale = new Vector3(rr, len / g.Len0, rr);
            return true;
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

        // ---- geometric push state (allocation-free once the arrays have grown) ----

        static Vector3[] _pA = new Vector3[0], _pB = new Vector3[0];   // each capsule's world endpoints last frame (indexed like SoftSim: by position in Caps)
        static bool[] _pOk = new bool[0];
        static byte[] _cat = new byte[0];                               // 1 = a source of geometric pushes (decided when the capsule set changes)
        static int _pN = -1, _pVer = -1, _catN = -1, _catVer = -1;
        static object _catList;
        static Transform _catRoot;
        static readonly Vector3[] _sa = new Vector3[Defs.Length], _sb = new Vector3[Defs.Length];   // her segments' world axes / centres this frame
        static readonly float[] _sr = new float[Defs.Length];
        static readonly bool[] _sOk = new bool[Defs.Length];
        // pushes seen (merged physics + geometric) for the 10 s log and the status
        static readonly float[] _sumF = new float[Defs.Length];
        static readonly int[] _nF = new int[Defs.Length];
        static readonly string[] _byLast = new string[Defs.Length];
        static float _tallyAt;
        static uint _pushBits;
        static string _statBase = "";

        // Which capsules can push her geometrically: your hand models and the partner's hands (the physics hands push through contact
        // impulses; her own capsules never push her). Decided when the capsule set changes, not per frame.
        static void Categorise(List<SoftCapsule> caps)
        {
            int n = caps.Count;
            if (_cat.Length < n) _cat = new byte[Mathf.Max(n, _cat.Length * 2)];
            for (int i = 0; i < n; i++)
            {
                var c = caps[i];
                bool ok = c.T != null && ((c.Kind == "vr" && c.Owner != "PhysicsHands") || c.Kind == "partner");
                if (ok && Root != null && c.T.IsChildOf(Root)) ok = false;
                _cat[i] = (byte)(ok ? 1 : 0);
            }
            _catN = n; _catVer = VrHands.Version; _catList = caps; _catRoot = Root;
        }

        static void ClosestSegSeg(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out float s, out Vector3 c1, out Vector3 c2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r), t;
            const float eps = 1e-8f;
            if (a <= eps && e <= eps) { s = 0f; t = 0f; }
            else if (a <= eps) { s = 0f; t = Mathf.Clamp01(f / e); }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= eps) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else
                {
                    float b = Vector3.Dot(d1, d2), den = a * e - b * b;
                    s = den > eps ? Mathf.Clamp01((b * f - c * e) / den) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
                }
            }
            c1 = p1 + d1 * s; c2 = p2 + d2 * t;
        }

        // Pushes from hands that aren't physics hands (your hand models, the partner's hands), by how deep they are in her SKIN shape (the
        // radius the capsule colliders had before the inset: RSkin x her bone scale): per capsule and segment a force along the line from the
        // capsule to her axis, Stiffness x depth + Damp x the capsule's approach speed, capped per hand and per segment. Fills GeoF / GeoPt / GeoBy
        // per segment every frame and sets _geoActive. (Physics.ComputePenetration would need a collider per capsule; the push only needs depth.)
        static void GeoPush(HerBodySettings s)
        {
            for (int i = 0; i < Segs.Count; i++)
            {
                var g = Segs[i];
                g.GeoF = Vector3.zero; g.GeoPt = Vector3.zero; g.GeoW = 0f; g.GeoDepth = 0f; g.GeoBy = null; g.GeoName = null; g.GeoVn = 0f;
            }
            _geoActive = false;
            var caps = s.Caps;
            if (!s.PushByTouch || caps == null || caps.Count == 0) { _pN = -1; return; }
            int n = caps.Count;
            if (n != _catN || VrHands.Version != _catVer || !ReferenceEquals(caps, _catList) || !ReferenceEquals(Root, _catRoot)) Categorise(caps);
            bool reset = n != _pN || VrHands.Version != _pVer;
            if (_pA.Length < n) { int m = Mathf.Max(n, _pA.Length * 2); _pA = new Vector3[m]; _pB = new Vector3[m]; _pOk = new bool[m]; reset = true; }
            if (reset) { Array.Clear(_pOk, 0, _pOk.Length); _pN = n; _pVer = VrHands.Version; }   // a different capsule set: no velocities from the old one
            float dt = Time.deltaTime, invDt = dt > 1e-4f && dt < 0.5f ? 1f / dt : 0f;

            // her segments' skin shapes in the world, once
            for (int i = 0; i < Segs.Count; i++)
            {
                var g = Segs[i];
                int k = g.Index;
                _sOk[k] = false;
                if (g.A == null || (!g.Sphere && g.B == null)) continue;
                float rr = RollScale(g);
                if (g.Sphere) { _sa[k] = _sb[k] = g.A.TransformPoint(g.LocalCenter); }
                else { _sa[k] = g.A.position; _sb[k] = g.B.position; }
                _sr[k] = g.RSkin * rr - Mathf.Min(SkinGive, Inset(g, s.Softness));   // the skin gives a little before the limb moves
                _sOk[k] = Finite(_sa[k]) && Finite(_sb[k]) && _sr[k] > 0.001f;
            }

            for (int ci = 0; ci < n; ci++)
            {
                var c = caps[ci];
                if (c.T == null || !(c.Radius > 0f)) { _pOk[ci] = false; continue; }
                Matrix4x4 m = c.T.localToWorldMatrix;
                Vector3 a = m.MultiplyPoint3x4(c.LocalA), b = m.MultiplyPoint3x4(c.LocalB);
                Vector3 va = Vector3.zero, vb = Vector3.zero;
                if (_pOk[ci] && invDt > 0f)
                {
                    va = (a - _pA[ci]) * invDt; vb = (b - _pB[ci]) * invDt;
                    float sa = va.sqrMagnitude, sbb = vb.sqrMagnitude;
                    if (sa > TeleportSpeed * TeleportSpeed || sbb > TeleportSpeed * TeleportSpeed) { va = Vector3.zero; vb = Vector3.zero; }   // jumped, not moved
                    else
                    {
                        if (sa > MaxCapSpeed * MaxCapSpeed) va *= MaxCapSpeed / Mathf.Sqrt(sa);
                        if (sbb > MaxCapSpeed * MaxCapSpeed) vb *= MaxCapSpeed / Mathf.Sqrt(sbb);
                    }
                }
                _pA[ci] = a; _pB[ci] = b; _pOk[ci] = true;   // tracked even while hidden so its speed is right when it comes back
                if (_cat[ci] == 0) continue;
                var src = c.Src;
                if (src != null && !src.Live) continue;      // a hidden partner (or replaced controller model) touches nothing
                if (!Finite(a) || !Finite(b)) continue;
                _geoActive = true;
                for (int i = 0; i < Segs.Count; i++)
                {
                    var g = Segs[i];
                    int k = g.Index;
                    if (!_sOk[k]) continue;
                    float t; Vector3 cc, cs;
                    ClosestSegSeg(a, b, _sa[k], _sb[k], out t, out cc, out cs);
                    Vector3 diff = cs - cc;
                    float dist = diff.magnitude;
                    float pen = c.Radius + _sr[k] - dist;
                    if (pen <= 0f || dist < 1e-4f) continue;
                    Vector3 nrm = diff / dist;                             // into her
                    float vn = Mathf.Max(0f, Vector3.Dot(Vector3.Lerp(va, vb, t), nrm));
                    if (vn > g.GeoVn) g.GeoVn = vn;
                    float mag = Mathf.Min(PushCapPerHand, PushStiffness * pen + PushDamp * vn);
                    Vector3 f = nrm * mag;
                    g.GeoF += f;
                    g.GeoPt += (cc + nrm * c.Radius) * mag;
                    g.GeoW += mag;
                    if (pen > g.GeoDepth) { g.GeoDepth = pen; g.GeoBy = src; g.GeoName = c.Owner; }
                }
            }

            for (int i = 0; i < Segs.Count; i++)
            {
                var g = Segs[i];
                if (g.GeoW > 1e-6f)
                {
                    g.GeoPt /= g.GeoW;
                    float fm = g.GeoF.magnitude;
                    if (fm > PushCapPerSeg) g.GeoF *= PushCapPerSeg / fm;
                    if (!Finite(g.GeoF) || !Finite(g.GeoPt)) { g.GeoF = Vector3.zero; g.GeoW = 0f; }
                }
                else g.GeoW = 0f;
                if (!g.PushLogged && g.GeoF.sqrMagnitude >= PushMinN * PushMinN)
                {
                    g.PushLogged = true;
                    Plugin.Log.LogInfo("Her body: first push on " + g.Name + " by " + ByName(g, 0f, 1f) + " (" + g.GeoF.magnitude.ToString("0.0") + " N, " + (g.GeoDepth * 100f).ToString("0.0") + " cm deep)");
                }
            }
        }

        // Nothing can push her: the last pushes must not linger.
        static void ClearPush()
        {
            for (int i = 0; i < Segs.Count; i++)
            {
                var g = Segs[i];
                g.GeoF = Vector3.zero; g.GeoW = 0f; g.PhysF = Vector3.zero;
            }
        }

        static string ByName(Seg g, float wp, float wg)
        {
            if (wp >= wg) return g.PushRight ? "your right physics hand" : "your left physics hand";
            return g.GeoBy != null ? g.GeoBy.Name : (g.GeoName != null ? g.GeoName : "a hand");
        }

        // Every frame: which segments are pushed (status line), and every 10 s one log line with their average force while pushes happened.
        static void Tally(float now)
        {
            uint bits = 0;
            for (int i = 0; i < Segs.Count; i++)
            {
                var g = Segs[i];
                Vector3 f = g.PhysF + g.GeoF;
                if (!PushActive || f.sqrMagnitude < PushMinN * PushMinN) continue;
                bits |= 1u << g.Index;
                float wp = g.PhysF.magnitude, wg = g.GeoF.magnitude;
                _sumF[g.Index] += f.magnitude; _nF[g.Index]++;
                _byLast[g.Index] = ByName(g, wp, wg);
            }
            if (bits != _pushBits)
            {
                _pushBits = bits;
                var sb = new StringBuilder(_statBase);
                bool first = true;
                for (int i = 0; i < Segs.Count; i++)
                    if ((bits & (1u << Segs[i].Index)) != 0) { sb.Append(first ? "; pushed: " : ", ").Append(Segs[i].Name); first = false; }
                Status = sb.ToString();
            }
            if (_tallyAt <= 0f) _tallyAt = now + 10f;
            else if (now >= _tallyAt)
            {
                _tallyAt = now + 10f;
                StringBuilder sb = null;
                for (int i = 0; i < Segs.Count; i++)
                {
                    int k = Segs[i].Index;
                    if (_nF[k] == 0) continue;
                    if (sb == null) sb = new StringBuilder("Her body: last 10 s pushes: "); else sb.Append(", ");
                    sb.Append(Segs[i].Name).Append(' ').Append((_sumF[k] / _nF[k]).ToString("0.0")).Append(" N avg by ").Append(_byLast[k]);
                    _sumF[k] = 0f; _nF[k] = 0;
                }
                if (sb != null) Plugin.Log.LogInfo(sb.ToString());
            }
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
            by = ByName(g, wp, wg);
            return true;
        }
    }
}
