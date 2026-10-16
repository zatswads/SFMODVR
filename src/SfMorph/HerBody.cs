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
    // 0.8.0: a contact capsule in WORLD space for this frame (no Transform): the physics hands' and Embody's ghosts for her yield
#pragma warning disable 649   // filled by PhysicsHands.FillGhost / Embody.FillGhost
    internal struct WorldCap
    {
        public Vector3 A, B; public float R;
        public int Hand;                // 0 left, 1 right, -1 unknown (which of YOUR hands it stands for)
        public string By;               // "your left physics hand", "your left hand (Player)": never built per frame
        public CapSource Src;           // for the Live gate / Reactions naming; may be null (= always live)
    }
#pragma warning restore 649

    internal class HerBodySettings
    {
        public bool On, PushByTouch = true;
        // partner hands: animation-driven, often resting on or holding her (his hands around her arms): they only push with
        // PushByPartner, except the partner you embody (his hands are your controllers then)
        public bool PushByPartner;
        public Transform Embodied;
        public float Softness = 0.5f;           // 0..1: how much her soft zones give (the inset of the collision shapes)
        public bool SoftZonesOn = true;         // 0.8.0: the soft zones run (they dent under a hand); off, every segment is inset like a firm one
        public List<SoftCapsule> Caps;           // Body.Contacts(): what can touch her this frame
    }

    // Her body as a set of segments (the pelvis, spine, chest, neck, head, thighs, calves, arms, butt and breast spheres): which bones
    // they follow, how big her skin is around them (read from her body meshes), where they stand now, and how hard hands push them.
    // The geometry is shared: the physics hands build PhysX bodies on it (PhysicsHands.cs), Overlap turns the YIELD (how deep any
    // hand is in her animation-pose skin, measured here before Overlap writes) into limb rotations, a geometric push for hands that
    // aren't physics hands plugs in here (GeoPush) and the analytic skin queries (SkinDepth, CapsuleDepth) serve the fingers. It exists whenever Physics or the
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
            // 0.8.0 yield (filled by the yield query, before Overlap): deepest penetration of any source into the skin shape (m, after
            // the skin give), the pen-weighted contact point and unit direction INTO her, who; zero/null when none
            public float YieldPen; public Vector3 YieldPt, YieldN; public string YieldBy; public int YieldHand; public float YieldW;
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
            public bool PushLogged, YieldLogged;
            // Space 1 (the head): where the shape's centre is in the segment-local frame (metres at scale 1); a fallback sphere collider
            // sits there (SegPose puts the frame at A, not at the sphere's centre). Zero for every other segment.
            public Vector3 FrameCenter;
            public float HullRMin, HullRMax;        // radial extent of the hull (about the axis, or about the centre): for logs and markers
            public float TouchTL = -100f, TouchTR = -100f;   // the same per hand (a resting hand's chatter must not hide the other hand's arrival)
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
        public static bool SoftZonesOn = true;                      // the same, of this frame (HerBodySettings.SoftZonesOn)
        public static float Softness = 0.5f;                        // the Physics tab's softness of this frame (HerBodySettings.Softness): Embody's clasp reads it
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
        // 0.8.0 yield (design-0.8.0 section 3): the deepest penetration below this is no yield; at most GhostMax world-space ghost capsules per frame
        const float YieldMinPen = 0.002f;
        const int GhostMax = 64;

        // ClaspGive (design 3.2): curl units (0..1 of a full curl) a clasp may sink past first contact on a segment at Softness 0.5
        // (0.1 curl ~ 8 deg at the proximal joint ~ 1 cm at the tip), by Defs index
        static readonly float[] _clasp = MakeClasp();
        static float[] MakeClasp()
        {
            var a = new float[Defs.Length];
            for (int i = 0; i < a.Length; i++)
            {
                string n = Defs[i].Name;
                a[i] = n.StartsWith("breast", StringComparison.Ordinal) || n.StartsWith("butt", StringComparison.Ordinal) ? 0.25f
                     : n.StartsWith("thigh", StringComparison.Ordinal) ? 0.15f : n == "pelvis" ? 0.10f : n == "spine" ? 0.12f : n == "chest" ? 0.08f
                     : n.StartsWith("upperarm", StringComparison.Ordinal) ? 0.06f : n.StartsWith("forearm", StringComparison.Ordinal) ? 0.04f
                     : n.StartsWith("calf", StringComparison.Ordinal) ? 0.05f : n == "neck" || n == "head" ? 0.03f : 0.10f;
            }
            return a;
        }

        static void LogOnce(string key, string msg) { if (_once.Add(key)) Plugin.Log.LogInfo("Her body: " + msg); }
        static bool Finite(Vector3 v) { return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)); }

        // ================= update =================

        // Per frame from Body.ApplyAll (after Apply, BEFORE Overlap: the bones hold the animation's pose, Overlap.Unwind saw to that):
        // builds her segments, rebuilds them when she changes (every 2 s: renderers, bones, character shape), runs the yield / push query.
        public static void Update(Character her, HerBodySettings s)
        {
            if (s == null || !s.On || her == null || her.Root == null) return;
            Softness = s.Softness; SoftZonesOn = s.SoftZonesOn;
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

        // How far the character's rest shape is morphed: the summed weights (0..1 each) of the first body mesh's body, face and slider shapes,
        // never the joint correctives (they follow the pose; MorphState.NonJcmSum). Feeds the rebuild trigger and the build log (a full
        // character reads about 200 %). 0 for the game's own mesh.
        static float MorphWeight(Character her)
        {
            foreach (var r in her.Renderers)
                if (r != null && r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0 && r.sharedMesh.name.ToLowerInvariant().Contains("body")) return MorphState.NonJcmSum(r);
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
            Built = false; Root = null; Status = ""; _dirty = false; _geoActive = false; HandsSegBits = 0;
            _pN = -1; _gN = -1; _catN = -1; _pushBits = 0; _tallyAt = 0f; _statBase = ""; _mixSig = 0;
            Array.Clear(_sOk, 0, _sOk.Length);
            Array.Clear(_sumF, 0, _sumF.Length); Array.Clear(_nF, 0, _nF.Length);
            Array.Clear(_sumY, 0, _sumY.Length); Array.Clear(_nY, 0, _nY.Length); Array.Clear(_maxY, 0, _maxY.Length);
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
        // 0.8.0 (user: the hands sank into her): the collider inset is how deep her TISSUE gives at that part, not a flat 2-4.5 cm for every
        // soft zone (a thigh has ~1.4 cm of flesh before the muscle; a hand stopped 3 cm inside it, deeper than any dent could show).
        // Metres at Softness 0.5, scaled lerp(0.7, 1.3, Softness); the same table bounds how far Embody's hand sinks before it stops.
        // Values after the contact-visuals Tissue table (butt ~23 mm, breast ~24, belly ~18, thigh 10-16, calf 9, upper arm 7-10, forearm 4).
        static float TissueDepth(string name)
        {
            if (name == null) return 0.004f;
            if (name.StartsWith("butt", StringComparison.Ordinal)) return 0.023f;
            if (name.StartsWith("breast", StringComparison.Ordinal)) return 0.024f;
            if (name == "spine") return 0.018f;        // the waist/belly segment
            if (name == "pelvis") return 0.012f;
            if (name.StartsWith("thigh", StringComparison.Ordinal)) return 0.014f;
            if (name.StartsWith("calf", StringComparison.Ordinal)) return 0.009f;
            if (name.StartsWith("upperarm", StringComparison.Ordinal)) return 0.009f;
            if (name.StartsWith("forearm", StringComparison.Ordinal)) return 0.004f;
            if (name == "chest") return 0.007f;
            if (name == "neck") return 0.004f;
            return 0.003f;                             // head, anything bony
        }
        static float Tissue(string name, float softness) { return TissueDepth(name) * Mathf.Lerp(0.7f, 1.3f, Mathf.Clamp01(softness)); }

        // With the soft zones off nothing dents, so only the skin's few millimetres give.
        // 0.8.9 (user: the physics hand still clips into her): the full tissue depth put the breast / butt colliders 2.4 cm inside her skin, but
        // the GPU dent follows a press exactly only to 0.6 of its tissue cap and saturates after (Tissue.Press), so the last ~1 cm of a press
        // showed the hand inside her. The collider now stops the hand where the dent still follows it.
        public const float InsetShare = 0.6f;
        public static float Inset(Seg g, float softness, bool softZonesOn) { return softZonesOn ? Mathf.Max(0.004f, InsetShare * Tissue(g.Name, softness)) : 0.004f; }
        public static float Inset(Seg g, float softness) { return Inset(g, softness, true); }

        // How far a hand may sink into the skin of segment `seg` (Defs index) before it stops, where the soft zones / GPU contact dent it:
        // the part's tissue depth (TissueDepth, by Softness) while the soft zones run, else 4 mm of skin. Reads this frame's settings.
        public static float SkinAllowance(int seg)
        {
            if (seg < 0 || seg >= Defs.Length || !SoftZonesOn) return 0.004f;
            return Mathf.Max(0.004f, InsetShare * Tissue(Defs[seg].Name, Softness));   // 0.8.9: where the dent still follows exactly
        }

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
                // A's matrix now, the frame's inverse): pose independent, includes the units and her scale, no sign or handedness guesses.
                // 0.8.0: through the roll bone instead when it is not A (the pelvis: its axis runs Hip_L to Hip_R but its skin is Root_M's).
                // Through Hip_L the pelvis vertices followed the thigh, so a build in a pose with the hip flexed relative to Root_M
                // squashed the hull (radius 6 mm, frame check 11 cm off) in 4 of 11 builds of the 0.7.9 log.
                var rig = new Matrix4x4[segs.Count]; var rigOk = new bool[segs.Count];
                for (int i = 0; i < segs.Count; i++)
                {
                    if (locs[i] == null) continue;
                    Transform rb = segs[i].RollRef != null && segs[i].RollRef != segs[i].A && segs[i].Space == 0 ? segs[i].RollRef : segs[i].A;
                    int ia = Array.IndexOf(bs, rb);
                    if (ia < 0 && rb != segs[i].A) { rb = segs[i].A; ia = Array.IndexOf(bs, rb); }
                    if (ia >= 0 && ia < d.Bind.Length) { rig[i] = fInv[i] * rb.localToWorldMatrix * d.Bind[ia]; rigOk[i] = true; }
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
                float inset = Inset(g, s.Softness, s.SoftZonesOn);
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
            HandsSegBits = (int)(SegBit("forearm_L") | SegBit("forearm_R"));
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
            var layout = MorphState.ShapeLayout(m);       // 0.8.0: the joint correctives follow the pose; baked in, they would shape her colliders by the pose of the build
            for (int k = 0; k < bc; k++)
            {
                bool jcm = false;
                for (int q = 0; q < layout.Count; q++) if (layout[q].Index == k) { jcm = layout[q].Kind == ShapeKind.Jcm; break; }
                if (jcm) continue;
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
        static readonly float[] _sr = new float[Defs.Length];        // shape radius for the yield / push: the skin minus the skin give
        static readonly float[] _srSkin = new float[Defs.Length];    // 0.8.0: the skin itself (RSkin * her bone's scale, no give): SkinDepth / CapsuleDepth
        static readonly bool[] _sOk = new bool[Defs.Length];
        // 0.8.0 world-space ghost capsules of this frame: [0, _nPhysGhost) the physics hands at the controllers (yield only), then Embody's
        static readonly WorldCap[] _ghost = new WorldCap[GhostMax];
        static int _nPhysGhost, _nEmbGhost, _gN = -1;
        static int _nVr, _nPartner, _nEmbExcl;                       // what Categorise found (the source mix log)
        static long _mixSig; static float _mixNext;
        // pushes seen (merged physics + geometric) for the 10 s log and the status
        static readonly float[] _sumF = new float[Defs.Length];
        static readonly int[] _nF = new int[Defs.Length];
        static readonly string[] _byLast = new string[Defs.Length];
        // yields seen: the pen sum / count / max and who, for the 10 s log
        static readonly float[] _sumY = new float[Defs.Length], _maxY = new float[Defs.Length];
        static readonly int[] _nY = new int[Defs.Length];
        static readonly string[] _byY = new string[Defs.Length];
        static float _tallyAt;
        static uint _pushBits;
        static string _statBase = "";

        // Which capsules can push her geometrically: your hand models and the partner's hands (the physics hands push through contact
        // impulses; her own capsules never push her). Decided when the capsule set changes, not per frame.
        // 0.8.0: the partner you embody is DROPPED (_catEmb): his hand transforms hold the animation's pose, not your controller, so
        // Embody.FillGhost stands for his hands. PushByPartner governs the other partner rigs.
        static bool _catPartner; static Transform _catEmb;
        static void Categorise(List<SoftCapsule> caps)
        {
            int n = caps.Count;
            if (_cat.Length < n) _cat = new byte[Mathf.Max(n, _cat.Length * 2)];
            _nVr = _nPartner = _nEmbExcl = 0;
            for (int i = 0; i < n; i++)
            {
                var c = caps[i];
                bool ok = false, vr = false;
                if (c.T != null)
                {
                    if (c.Kind == "vr" && c.Owner != "PhysicsHands") { ok = true; vr = true; }
                    else if (c.Kind == "partner")
                    {
                        if (_catEmb != null && c.T.IsChildOf(_catEmb)) _nEmbExcl++;
                        else ok = _catPartner;
                    }
                }
                if (ok && Root != null && c.T.IsChildOf(Root)) ok = false;
                _cat[i] = (byte)(ok ? 1 : 0);
                if (ok) { if (vr) _nVr++; else _nPartner++; }
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

        // Yield and push sources (0.8.0 rewrite of the 0.7.8 geometric push). Every frame, BEFORE Overlap (HerBody.Update runs first, against
        // the animation's pose: Overlap.Unwind), for each source capsule and each of her segments, by how deep the capsule is in her SKIN
        // shape (the radius the capsule colliders had before the inset: RSkin x her bone scale, minus the skin give):
        //  - YIELD (all sources): pen-weighted point and direction into her, the deepest penetration, who. Overlap turns it into limb
        //    rotations (a position-based displacement, not a force).
        //  - PUSH (list capsules and Embody's ghosts, not the physics ghosts): Stiffness x depth + Damp x approach speed along the line
        //    from the capsule to her axis, capped per hand and per segment: GeoF / GeoPt / GeoBy / GeoDepth / GeoVn, what Reactions reads.
        // Sources: Body.Contacts() capsules that Categorise accepted (your hand models, other partners' hands), then the world-space ghosts:
        // the physics hands at the CONTROLLER pose (PhysicsHands.FillGhost; yield only: their contact already reaches Reactions through
        // the PhysX hits) and Embody's hands at the IK target (Embody.FillGhost; both). Physics.ComputePenetration would need a collider per
        // capsule; this only needs depth.
        static void GeoPush(HerBodySettings s)
        {
            for (int i = 0; i < Segs.Count; i++)
            {
                var g = Segs[i];
                g.GeoF = Vector3.zero; g.GeoPt = Vector3.zero; g.GeoW = 0f; g.GeoDepth = 0f; g.GeoBy = null; g.GeoName = null; g.GeoVn = 0f;
                g.YieldPen = 0f; g.YieldPt = Vector3.zero; g.YieldN = Vector3.zero; g.YieldBy = null; g.YieldHand = -1; g.YieldW = 0f;
            }
            _geoActive = false;
            ShapesNow();                                    // also for SkinDepth / CapsuleDepth, whatever the sources are
            var caps = s.Caps;
            bool listOn = s.PushByTouch && caps != null && caps.Count > 0;
            int n = listOn ? caps.Count : 0;
            _nPhysGhost = Mathf.Clamp(PhysicsHands.FillGhost(_ghost, 0, GhostMax), 0, GhostMax);
            // the physics hands' ghosts are not a "touch push" (PushByTouch is about hands that aren't physics hands); Embody's are
            _nEmbGhost = s.PushByTouch ? Mathf.Clamp(Embody.FillGhost(_ghost, _nPhysGhost, GhostMax - _nPhysGhost), 0, GhostMax - _nPhysGhost) : 0;
            int ng = _nPhysGhost + _nEmbGhost;
            if (n == 0) { _nVr = _nPartner = _nEmbExcl = 0; _catN = -1; }   // no list: no categories (and the next list is categorised afresh)
            if (n > 0 && (n != _catN || VrHands.Version != _catVer || !ReferenceEquals(caps, _catList) || !ReferenceEquals(Root, _catRoot)
                || s.PushByPartner != _catPartner || !ReferenceEquals(s.Embodied, _catEmb))) { _catPartner = s.PushByPartner; _catEmb = s.Embodied; Categorise(caps); }
            LogMix(s);
            if (n == 0 && ng == 0) { _pN = -1; _gN = -1; return; }
            bool reset = n != _pN || VrHands.Version != _pVer || ng != _gN;
            int need = n + GhostMax;
            if (_pA.Length < need) { int m = Mathf.Max(need, _pA.Length * 2); _pA = new Vector3[m]; _pB = new Vector3[m]; _pOk = new bool[m]; reset = true; }
            if (reset) { Array.Clear(_pOk, 0, _pOk.Length); _pN = n; _pVer = VrHands.Version; _gN = ng; }   // a different capsule set: no velocities from the old one
            float dt = Time.deltaTime, invDt = dt > 1e-4f && dt < 0.5f ? 1f / dt : 0f;

            for (int ci = 0; ci < n; ci++)
            {
                var c = caps[ci];
                if (c.T == null || !(c.Radius > 0f)) { _pOk[ci] = false; continue; }
                Matrix4x4 m = c.T.localToWorldMatrix;
                Vector3 a = m.MultiplyPoint3x4(c.LocalA), b = m.MultiplyPoint3x4(c.LocalB);
                Vector3 va, vb, raA, raB;   // raA/raB: the same velocities before the MaxCapSpeed clamp: what the reactions classify a slap by
                Velocity(ci, a, b, invDt, out va, out vb, out raA, out raB);   // tracked even while hidden so its speed is right when it comes back
                if (_cat[ci] == 0) continue;
                var src = c.Src;
                if (src != null && !src.Live) continue;      // a hidden partner (or replaced controller model) touches nothing
                if (!Finite(a) || !Finite(b)) continue;
                _geoActive = true;
                string who = src != null && src.Name != null ? src.Name : (c.Owner ?? "a hand");
                Feed(a, b, c.Radius, src, c.Owner, who, HandOf(who), true, va, vb, raA, raB);
            }
            for (int gi = 0; gi < ng; gi++)
            {
                var w = _ghost[gi];
                if (!(w.R > 0f) || !Finite(w.A) || !Finite(w.B)) continue;
                if (w.Src != null && !w.Src.Live) continue;
                bool geo = gi >= _nPhysGhost;                // Embody's: geometric hands; the physics hands' yield only
                Vector3 va = Vector3.zero, vb = Vector3.zero, raA = Vector3.zero, raB = Vector3.zero;
                if (geo) Velocity(n + gi, w.A, w.B, invDt, out va, out vb, out raA, out raB);
                _geoActive = true;
                Feed(w.A, w.B, w.R, w.Src, w.By, w.By ?? "a hand", w.Hand, geo, va, vb, raA, raB);
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
                if (g.YieldW > 1e-6f)
                {
                    g.YieldPt /= g.YieldW;
                    float nm = g.YieldN.magnitude;
                    if (nm > 1e-6f) g.YieldN /= nm;
                    if (!(nm > 1e-6f) || !Finite(g.YieldPt) || !Finite(g.YieldN)) { g.YieldPen = 0f; g.YieldW = 0f; g.YieldPt = g.YieldN = Vector3.zero; g.YieldBy = null; }
                }
                else { g.YieldW = 0f; g.YieldPen = 0f; }
                if (!g.PushLogged && g.GeoF.sqrMagnitude >= PushMinN * PushMinN)
                {
                    g.PushLogged = true;
                    Plugin.Log.LogInfo("Her body: first push on " + g.Name + " by " + ByName(g, 0f, 1f) + " (" + g.GeoF.magnitude.ToString("0.0") + " N, " + (g.GeoDepth * 100f).ToString("0.0") + " cm deep)");
                }
                if (!g.YieldLogged && g.YieldPen >= YieldMinPen)
                {
                    g.YieldLogged = true;
                    Plugin.Log.LogInfo("Her body: first yield on " + g.Name + " by " + (g.YieldBy ?? "a hand") + " (" + (g.YieldPen * 100f).ToString("0.0") + " cm deep)");
                }
            }
        }

        // her segments' skin shapes in the world, once per frame: the axis, the radius the push / yield use (skin - give) and the skin radius
        static void ShapesNow()
        {
            for (int i = 0; i < Segs.Count; i++)
            {
                var g = Segs[i];
                int k = g.Index;
                _sOk[k] = false;
                if (g.A == null || (!g.Sphere && g.B == null)) continue;
                float rr = RollScale(g);
                if (g.Sphere) { _sa[k] = _sb[k] = g.A.TransformPoint(g.LocalCenter); }
                else { _sa[k] = g.A.position; _sb[k] = g.B.position; }
                _srSkin[k] = g.RSkin * rr;
                _sr[k] = _srSkin[k] - Mathf.Min(SkinGive, Inset(g, Softness, SoftZonesOn));   // the skin gives a little before the limb moves
                _sOk[k] = Finite(_sa[k]) && Finite(_sb[k]) && _sr[k] > 0.001f;
            }
        }

        // 0.8.0: the skin shapes on her LIVE bones (after Overlap wrote the yield / lag): Embody's stop, clasp and swivel must see where her limb
        // IS. GeoPush puts them back on the animation pose at the next frame's query, so the yield itself stays open-loop.
        public static void RefreshShapes() { if (Built) ShapesNow(); }

        // The capsule's world velocity from last frame's endpoints (slot si), with the MaxCapSpeed clamp and without it; a jump is not motion.
        static void Velocity(int si, Vector3 a, Vector3 b, float invDt, out Vector3 va, out Vector3 vb, out Vector3 raA, out Vector3 raB)
        {
            va = vb = raA = raB = Vector3.zero;
            if (_pOk[si] && invDt > 0f)
            {
                va = (a - _pA[si]) * invDt; vb = (b - _pB[si]) * invDt;
                float sa = va.sqrMagnitude, sbb = vb.sqrMagnitude;
                if (sa > TeleportSpeed * TeleportSpeed || sbb > TeleportSpeed * TeleportSpeed) { va = Vector3.zero; vb = Vector3.zero; }   // jumped, not moved
                else
                {
                    raA = va; raB = vb;
                    if (sa > MaxCapSpeed * MaxCapSpeed) va *= MaxCapSpeed / Mathf.Sqrt(sa);
                    if (sbb > MaxCapSpeed * MaxCapSpeed) vb *= MaxCapSpeed / Mathf.Sqrt(sbb);
                }
            }
            _pA[si] = a; _pB[si] = b; _pOk[si] = true;
        }

        // One source capsule against every segment (see GeoPush). who/hand: for the yield; owner: GeoName; geo false = yield only.
        static void Feed(Vector3 a, Vector3 b, float rad, CapSource src, string owner, string who, int hand, bool geo, Vector3 va, Vector3 vb, Vector3 raA, Vector3 raB)
        {
            for (int i = 0; i < Segs.Count; i++)
            {
                var g = Segs[i];
                int k = g.Index;
                if (!_sOk[k]) continue;
                float t; Vector3 cc, cs;
                ClosestSegSeg(a, b, _sa[k], _sb[k], out t, out cc, out cs);
                Vector3 diff = cs - cc;
                float dist = diff.magnitude;
                float pen = rad + _sr[k] - dist;
                if (pen <= 0f || dist < 1e-4f) continue;
                Vector3 nrm = diff / dist;                             // into her
                // yield: pen-weighted so the point and direction stay continuous when the deepest capsule changes (a palm and five fingers on a thigh)
                g.YieldW += pen; g.YieldPt += (cc + nrm * rad) * pen; g.YieldN += nrm * pen;
                if (pen > g.YieldPen) { g.YieldPen = pen; g.YieldBy = who; g.YieldHand = hand; }
                if (!geo) continue;
                float vn = Mathf.Max(0f, Vector3.Dot(Vector3.Lerp(va, vb, t), nrm));
                float vnRaw = Mathf.Max(0f, Vector3.Dot(Vector3.Lerp(raA, raB, t), nrm));   // unclamped: a hand-model slap above the push clamp still reads as one
                if (vnRaw > g.GeoVn) g.GeoVn = vnRaw;
                float mag = Mathf.Min(PushCapPerHand, PushStiffness * pen + PushDamp * vn);
                Vector3 f = nrm * mag;
                g.GeoF += f;
                g.GeoPt += (cc + nrm * rad) * mag;
                g.GeoW += mag;
                if (pen > g.GeoDepth) { g.GeoDepth = pen; g.GeoBy = src; g.GeoName = owner; }
            }
        }

        // Which of YOUR hands a source name stands for ("your left hand", "your right physics hand"), -1 when it doesn't say.
        static int HandOf(string who)
        {
            if (who == null) return -1;
            if (who.IndexOf("left", StringComparison.Ordinal) >= 0) return 0;
            if (who.IndexOf("right", StringComparison.Ordinal) >= 0) return 1;
            return -1;
        }

        // The source mix, logged when it changes (at most every 2 s).
        static void LogMix(HerBodySettings s)
        {
            long sig = 1L + _nVr + ((long)_nPartner << 8) + ((long)_nEmbExcl << 16) + ((long)_nPhysGhost << 24) + ((long)_nEmbGhost << 32);
            if (_nVr + _nPartner + _nEmbExcl + _nPhysGhost + _nEmbGhost == 0) sig = 0;
            if (sig == _mixSig) return;
            float now = Time.unscaledTime;
            if (now < _mixNext) return;
            _mixNext = now + 2f; _mixSig = sig;
            if (sig != 0)
                Plugin.Log.LogInfo("Her body: yield sources: hand models " + _nVr + ", partners " + _nPartner + " (embodied excluded: " + _nEmbExcl + "), physics ghosts " + _nPhysGhost + ", his ghosts " + _nEmbGhost);
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

        // Every frame: which segments yield or are pushed (status line), and every 10 s one log line with their average force and
        // penetration while that happened.
        static void Tally(float now)
        {
            uint bits = 0;
            for (int i = 0; i < Segs.Count; i++)
            {
                var g = Segs[i];
                int k = g.Index;
                Vector3 f = g.PhysF + g.GeoF;
                bool pushed = PushActive && f.sqrMagnitude >= PushMinN * PushMinN;
                bool yielded = g.YieldPen >= YieldMinPen;
                if (!pushed && !yielded) continue;
                bits |= 1u << k;
                if (pushed)
                {
                    float wp = g.PhysF.magnitude, wg = g.GeoF.magnitude;
                    _sumF[k] += f.magnitude; _nF[k]++;
                    _byLast[k] = ByName(g, wp, wg);
                }
                if (yielded)
                {
                    _sumY[k] += g.YieldPen; _nY[k]++;
                    if (g.YieldPen > _maxY[k]) _maxY[k] = g.YieldPen;
                    _byY[k] = g.YieldBy;
                }
            }
            if (bits != _pushBits)
            {
                _pushBits = bits;
                var sb = new StringBuilder(_statBase);
                bool first = true;
                for (int i = 0; i < Segs.Count; i++)
                    if ((bits & (1u << Segs[i].Index)) != 0) { sb.Append(first ? "; yielding: " : ", ").Append(Segs[i].Name); first = false; }
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
                StringBuilder sy = null;
                for (int i = 0; i < Segs.Count; i++)
                {
                    int k = Segs[i].Index;
                    if (_nY[k] == 0) continue;
                    if (sy == null) sy = new StringBuilder(sb != null ? "; yield: " : "Her body: last 10 s yield: "); else sy.Append(", ");
                    sy.Append(Segs[i].Name).Append(' ').Append((_sumY[k] / _nY[k] * 100f).ToString("0.0")).Append(" cm avg ").Append((_maxY[k] * 100f).ToString("0.0")).Append(" max by ").Append(_byY[k] ?? "a hand");
                    _sumY[k] = 0f; _nY[k] = 0; _maxY[k] = 0f;
                }
                if (sb != null) { if (sy != null) sb.Append(sy); Plugin.Log.LogInfo(sb.ToString()); }
                else if (sy != null) Plugin.Log.LogInfo(sy.ToString());
            }
        }

        // ---- 0.8.0 yield and analytic queries (design-0.8.0 sections 2, 3.1, 3.2) ----

        // The yield of one of her segments this frame (Overlap's input), measured by GeoPush before Overlap against the animation's pose:
        // the pen-weighted contact point (world), the unit direction INTO her, the deepest penetration after the skin give (m), who and
        // which of YOUR hands (0 left, 1 right, -1 unknown). False below YieldMinPen (2 mm), when nothing is built or the index is bad.
        public static bool YieldOn(int segIndex, out Vector3 point, out Vector3 normalIntoHer, out float pen, out string by, out int hand)
        {
            point = Vector3.zero; normalIntoHer = Vector3.zero; pen = 0f; by = null; hand = -1;
            if (!Built || segIndex < 0 || segIndex >= _byIndex.Length) return false;
            var g = _byIndex[segIndex];
            if (g == null || !(g.YieldPen >= YieldMinPen) || !(g.YieldW > 0f)) return false;
            point = g.YieldPt; normalIntoHer = g.YieldN; pen = g.YieldPen; by = g.YieldBy; hand = g.YieldHand;
            return true;
        }

        // Curl units (0..1 of a full curl): how far a clasp may sink past first contact on this segment, before the caller scales it by
        // (0.5 + Softness). 0.1 for an index outside the segments.
        public static float ClaspGive(int segIndex) { return segIndex >= 0 && segIndex < _clasp.Length ? _clasp[segIndex] : 0.1f; }

        // The deepest of her segments for a sphere (p, r) against the SKIN shapes (RSkin x her bone scale, no give: a finger stops at the
        // skin, the soft zones dent under it). depth > 0 inside (the sphere overlaps the skin by that much); false = none within r.
        // Uses the shapes GeoPush placed this frame (HerBody.Update ran): 17 point-capsule tests.
        public static bool SkinDepth(Vector3 p, float r, out int segIndex, out float depth, out Vector3 normalIntoHer)
        {
            segIndex = -1; depth = 0f; normalIntoHer = Vector3.zero;
            if (!Built || !Finite(p) || float.IsNaN(r)) return false;
            for (int i = 0; i < Segs.Count; i++)
            {
                int k = Segs[i].Index;
                if (!_sOk[k]) continue;
                Vector3 ab = _sb[k] - _sa[k];
                float l2 = ab.sqrMagnitude;
                float t = l2 > 1e-10f ? Mathf.Clamp01(Vector3.Dot(p - _sa[k], ab) / l2) : 0f;
                Vector3 c = _sa[k] + ab * t, diff = c - p;
                float dist = diff.magnitude;
                float d = r + _srSkin[k] - dist;
                if (d <= depth) continue;
                depth = d; segIndex = k;
                // a centre on the axis has no direction: perpendicular to the axis (any), else down
                normalIntoHer = dist > 1e-5f ? diff / dist : (l2 > 1e-10f ? Vector3.Cross(ab, Vector3.up).normalized : Vector3.down);
            }
            return segIndex >= 0;
        }

        // The deepest penetration (m) of a capsule (a, b, r) into the skin shapes, 0 when none; skipSegBits = segments to ignore
        // (1u << Seg.Index, SegBit). segIndex / normalIntoHer describe the deepest one.
        public static float CapsuleDepth(Vector3 a, Vector3 b, float r, uint skipSegBits, out int segIndex, out Vector3 normalIntoHer)
        {
            segIndex = -1; normalIntoHer = Vector3.zero;
            if (!Built || !Finite(a) || !Finite(b) || float.IsNaN(r)) return 0f;
            float best = 0f;
            for (int i = 0; i < Segs.Count; i++)
            {
                int k = Segs[i].Index;
                if (!_sOk[k] || (skipSegBits & (1u << k)) != 0u) continue;
                float t; Vector3 cc, cs;
                ClosestSegSeg(a, b, _sa[k], _sb[k], out t, out cc, out cs);
                Vector3 diff = cs - cc;
                float dist = diff.magnitude;
                float d = r + _srSkin[k] - dist;
                if (d <= best || dist < 1e-5f) continue;
                best = d; segIndex = k; normalIntoHer = diff / dist;
            }
            return best;
        }

        // The bit of a segment by name (1u << Seg.Index), 0 when it is not built.
        public static uint SegBit(string segName) { var g = Find(segName); return g != null ? 1u << g.Index : 0u; }
        public static int HandsSegBits;                              // forearm_L/R (her hands have no segment): never 'her own hands' for CapsuleDepth callers

        // The push on one of her segments over the last step: the force ON HER (N, world), where it acted, and who pushed ("your right
        // physics hand", "Player", ...). The physics hands' contact force (PhysF) and the geometric push (GeoF) add up; false below 0.2 N.
        // Overlap no longer reads it (0.8.0: YieldOn); it stays for diagnostics.
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
