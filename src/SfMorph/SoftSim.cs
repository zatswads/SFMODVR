using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // The soft zones' simulation (see SoftZones.cs for what the points and helper bones are). Every frame after Body applies:
    // each helper's rest place R follows its bone, a spring holds the displacement Disp (world space) to it, contact
    // capsules (VrHands, PhysicsHands) imprint the point (it is moved along its inward normal to where that line leaves the
    // capsule, so the dent has the capsule's profile; a radial push when that fails) and hand it some of their speed, the
    // displacement is clamped to what skin can do, a hit spreads a little to the zone's other points, and S is written at
    // R + Disp. How deep each physics hand is in feeds back as LeftResist/RightResist (PhysicsHands caps the hand's force
    // with it). No allocations per frame: state lives in preallocated arrays and fields; strings are rebuilt only when the
    // contacts change.
    internal partial class SoftZones
    {
        const float Step = 1f / 120f;                  // fixed substep: the springs behave the same at any frame rate
        const int MaxSteps = 4;
        // 0.7.7: the reach of a capsule is its radius plus this. The points sit ON the skin (SoftZones picks skin vertices), so a capsule
        // dents exactly when its surface reaches the skin; the 0.7.6 PointRadius 12 mm + Margin 8 mm made every hand dent from 2 cm away,
        // fatter than itself, and made the physics hands' force cap fall before visible contact. 3 mm is GpuContact's margin too.
        const float SkinMargin = 0.003f;
        const int MaxCaps = 256;
        const float MaxCapSpeed = 3f, TeleportSpeed = 8f;    // m/s: faster capsules are clamped, much faster ones jumped
        const float SagOutward = 0.6f;                       // 0.7.3: how far (x limit) the sag may carry a point outward; skin otherwise rises 0.3 limit

        // ---- state of the current build ----

        Matrix4x4[] _bL2W, _bW2L;      // per distinct bone, this frame
        bool[] _bOk;
        int[] _zH0, _zH1;              // per zone: its helpers [H0, H1) in _helpers (they are built zone by zone), -1 = none
        float[] _zSigma;               // per zone: spacing of its points (m), the reach of a hit
        float[] _zBulge;               // per zone: how much of a push its other points get (0.25 at Performance; less with more points)
        float[] _zSag;                 // 0.7.3 per zone: how much gravity pulls it (x limit x gravity): breast 1, belly 0.5, glute 0.6, thigh 0.3
        Vector3[] _zSagV;              // per zone, this frame: the world-space sag (down * _zSag * limit * gravity)
        float _acc;                    // fixed-step accumulator

        // ---- the capsules this frame ----

        Vector3[] _cA = new Vector3[0], _cB = new Vector3[0], _cPA = new Vector3[0], _cPB = new Vector3[0], _cVA = new Vector3[0], _cVB = new Vector3[0], _cMid = new Vector3[0];
        float[] _cR = new float[0], _cReach2 = new float[0];
        int[] _cCode = new int[0];     // 0 any zone, 1 her left thigh (only the right inner thigh feels it), 2 her right thigh
        int[] _cHand = new int[0];     // 0 not a physics hand, 1 the left physics hand, 2 the right one (see HandOf)
        bool[] _cOk = new bool[0], _cHit = new bool[0];
        CapSource[] _cSrc = new CapSource[0];   // 0.7.7: who each capsule belongs to (null = ungated, no source)
        bool[] _cLive = new bool[0];            // 0.7.7: false = its source is hidden: it moves (velocities stay right when it reappears) but touches nothing
        int[] _cId = new int[0];                // 0.7.7: its source's Id, 0 = no source
        string[] _cLabel = new string[0];       // 0.7.7: "Owner/Kind" for a capsule with no source (built when the set changes, not per frame)
        int _cN, _capVersion = -1;

        // ---- contacts ----

        int[] _zCnt;                   // per zone: points touched this frame
        bool[] _zSeen;                 // per zone * MaxSources + source id: first contact logged (once per zone and source per build)
        readonly int[] _srcCnt = new int[VrHands.MaxSources];            // 0.7.7: points touched this frame per source id (a point two sources touch counts for both)
        readonly CapSource[] _srcOf = new CapSource[VrHands.MaxSources]; // the source behind each id this frame (names for the status)
        string _noSrcLabel = "";       // Owner/Kind of the capsules without a source that touched her this frame
        int _stamp, _nContacts;
        // 0.8.0 impacts (design 4.1): a fresh fast contact on a soft zone -> Impacts (Ripple.Collect reads them after Simulate)
        const float ImpactMinSpeed = 0.7f, ImpactGap = 0.3f;
        // 0.11.7 (user: the ripple should also come from the animation, his hips): his pelvis and thighs stay on her during sex, so the fresh
        // contact rule above only fired on the first touch. Their strokes count instead: a forward approach of 0.35 m/s or more, at most one
        // per 0.35 s per zone and source (a thrust every 0.4-1 s in the game's loops).
        const float ThrustMinSpeed = 0.35f, ThrustGap = 0.35f;
        public static bool ThrustRipple = true;   // Body sets it from [Physics] RippleThrust
        // 0.11.12 (user: the thrust ripple does not react; log: 30 impacts in the whole run, his at 0.4 m/s -> 1 mm waves): his hips are fastest
        // mid-stroke and nearly still when they reach her, so the approach measured at the touch was small and often under the 0.35 m/s gate.
        // A stroke is now: his pelvis / thigh capsule closing on her (relative to her own motion: the centroid of her soft points) and backing off
        // again; the first touch of each forward stroke fires once, with the stroke's peak closing speed.
        float[] _cPeak = new float[0];   // per capsule: decaying peak closing speed toward her (m/s)
        bool[] _cAway = new bool[0];     // per capsule: it backed off since its last stroke
        Vector3 _herC, _herCPrev; bool _herCOk;
        const float StrokeBack = 0.08f, StrokeTau = 0.35f;   // m/s of backing off that re-arms a stroke; s the peak decays over
        bool[] _cBody = new bool[0];     // m/s approach along the skin's inward normal; s between impacts of one source / zone
        // 0.8.0 Tissue.Zone: per zone stiffness / depth / damping multipliers (set at build) and this frame's values
        float[] _zKMul, _zLimMul, _zDampMul, _zK, _zLim, _zDamp;
        ulong[] _hPrevSrc;                                       // per helper: its SrcMask of the previous Simulate (is this source new on it?)
        float[] _zLastImpact, _zLastApproach;                    // per zone: when and how fast the last impact was
        readonly float[] _srcLastImpact = new float[VrHands.MaxSources];
        long _sig = -1;
        float _nextSummary, _peak;
        float _rL, _rR, _peakRL, _peakRR, _peakDL, _peakDR;   // smoothed resist per physics hand; peaks (resist, dent in m) since the last log line
        bool _ownsResist;
        readonly StringBuilder _sb = new StringBuilder();
        public string Contacts = "";

        void SimBuilt(int[] zoneH0, int[] zoneH1)
        {
            _zH0 = zoneH0; _zH1 = zoneH1;
            _zSigma = new float[Zones.Length]; _zBulge = new float[Zones.Length]; _zCnt = new int[Zones.Length]; _zSeen = new bool[Zones.Length * VrHands.MaxSources];
            _zSag = new float[Zones.Length]; _zSagV = new Vector3[Zones.Length];
            for (int z = 0; z < Zones.Length; z++) _zSag[z] = SagOf(Zones[z]);
            var pts = new int[Zones.Length];
            foreach (var p in _pts) pts[p.ZoneIdx]++;
            for (int z = 0; z < Zones.Length; z++)
            {
                // the reach of a hit stays what it was in 0.7.0 at every quality (the 0.7.0 ring of K-1 points at 0.55 R); with more points the
                // amount each pushes its neighbours is scaled down by the density, so the bulge stays about as large
                int ring = (_bq == 0 ? pts[z] : Zones[z].K) - 1;
                _zSigma[z] = Mathf.Max(0.02f, ring >= 2 ? 0.55f * Zones[z].Radius * 2f * Mathf.Sin(Mathf.PI / ring) : 0.55f * Zones[z].Radius);
                _zBulge[z] = Mathf.Min(0.25f, 0.25f * Zones[z].K / Mathf.Max(1, pts[z]));
            }
            _rL = _rR = 0f; _peakRL = _peakRR = _peakDL = _peakDR = 0f;
            _zKMul = new float[Zones.Length]; _zLimMul = new float[Zones.Length]; _zDampMul = new float[Zones.Length];
            _zK = new float[Zones.Length]; _zLim = new float[Zones.Length]; _zDamp = new float[Zones.Length];
            for (int z = 0; z < Zones.Length; z++) Tissue.Zone(Zones[z].Id, out _zKMul[z], out _zLimMul[z], out _zDampMul[z]);
            _hPrevSrc = new ulong[_helpers.Count]; _zLastImpact = new float[Zones.Length]; _zLastApproach = new float[Zones.Length];
            for (int z = 0; z < Zones.Length; z++) _zLastImpact[z] = -10f;
            for (int i = 0; i < _srcLastImpact.Length; i++) _srcLastImpact[i] = -10f;
            ImpactCount = 0;
            _bL2W = new Matrix4x4[_bones.Count]; _bW2L = new Matrix4x4[_bones.Count]; _bOk = new bool[_bones.Count];
            _acc = 0f; _capVersion = -1; _nContacts = 0; _sig = -1; _stamp = 0; _peak = 0f;
        }

        void SimReset()
        {
            DebugOff();
            _bL2W = _bW2L = null; _bOk = null; _zH0 = _zH1 = null; _zSigma = null; _zBulge = null; _zCnt = null; _zSeen = null; _zSag = null; _zSagV = null;
            _hPrevSrc = null; _zLastImpact = _zLastApproach = null; ImpactCount = 0;
            _zKMul = _zLimMul = _zDampMul = _zK = _zLim = _zDamp = null;
            _nContacts = 0; _sig = -1;
            _rL = _rR = 0f;
            if (_ownsResist) { LeftResist = RightResist = 0f; _ownsResist = false; }
        }

        static float SagOf(Zone z)
        {
            switch (z.Label)
            {
                // 0.7.7: no patch sag on the breast. The game's breast DynamicBone already hangs the whole breast; sliding only the front patch
                // (the areola) 1.6 cm down, or pressing it in 3 cm when she lies back, read as a distorted areola with nothing touching.
                case "breast front": case "breast side": return 0f;
                case "belly": return 0.5f;
                case "glute": return 0.6f;
                default: return 0.3f;   // thighs
            }
        }

        void EnsureCaps(int n)
        {
            if (_cA.Length >= n) return;
            int m = Math.Max(n, Math.Max(16, _cA.Length * 2));
            _cA = new Vector3[m]; _cB = new Vector3[m]; _cPA = new Vector3[m]; _cPB = new Vector3[m]; _cVA = new Vector3[m]; _cVB = new Vector3[m]; _cMid = new Vector3[m];
            _cR = new float[m]; _cReach2 = new float[m]; _cCode = new int[m]; _cHand = new int[m]; _cOk = new bool[m]; _cHit = new bool[m];
            _cSrc = new CapSource[m]; _cLive = new bool[m]; _cId = new int[m]; _cLabel = new string[m]; _cBody = new bool[m];
            _capVersion = -1;   // the old previous positions are gone
        }

        // Which physics hand a capsule belongs to: 1 left, 2 right, 0 none. A physics hand's capsule has Kind "vr" and is made by PhysicsHands
        // (Owner "PhysicsHands" in 0.7.0; the SfPhys* naming from 0.7.2 on). It counts when its Owner starts with "SfPhys" or is "PhysicsHands",
        // or when a transform above it is named SfPhys*; the side is read from the Owner, else from that SfPhys* ancestor's name (PhysicsHands names
        // its roots SfPhys_L_visual / SfPhys_R_visual): "Left"/"Right" anywhere, or "_L"/"_R" as a whole token ("SfPhys_L_visual", "SfPhysHand_R").
        // Everything else (the controllers' "vr" capsules of VrHands, partners, her own) gives no feedback. Only called when the capsule set changes.
        static int HandOf(SoftCapsule cp)
        {
            if (cp.Kind != "vr") return 0;
            string o = cp.Owner ?? "";
            bool phys = o.StartsWith("SfPhys", StringComparison.Ordinal) || o == "PhysicsHands";
            int side = SideOf(o);
            for (var t = cp.T; t != null && (side == 0 || !phys); t = t.parent)
            {
                string n = t.name;
                if (!n.StartsWith("SfPhys", StringComparison.Ordinal)) continue;
                phys = true;
                if (side == 0) side = SideOf(n);
            }
            return phys ? side : 0;
        }

        static int SideOf(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            if (s.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (s.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            if (s.IndexOf("_L_", StringComparison.Ordinal) >= 0 || s.EndsWith("_L", StringComparison.Ordinal)) return 1;
            if (s.IndexOf("_R_", StringComparison.Ordinal) >= 0 || s.EndsWith("_R", StringComparison.Ordinal)) return 2;
            return 0;
        }

        // Ray p + t d (d unit) against the capsule a, a+ab with radius r, p inside it: the largest t >= 0 where the ray leaves it. The capsule is
        // convex, so the ray meets it in one interval, which is the union of its pieces' intervals (the cylinder between the end planes, the two end
        // spheres): the exit is the largest exit among them.
        static bool RayExit(Vector3 p, Vector3 d, Vector3 a, Vector3 ab, float len2, float r, out float tExit)
        {
            tExit = -1f;
            bool any = false;
            float r2 = r * r;
            for (int k = 0; k < 2; k++)
            {
                Vector3 oc = p - (k == 0 ? a : a + ab);
                float b = Vector3.Dot(oc, d), disc = b * b - (oc.sqrMagnitude - r2);
                if (disc < 0f) continue;
                float t1 = -b + Mathf.Sqrt(disc);
                if (t1 >= 0f && (!any || t1 > tExit)) { tExit = t1; any = true; }
            }
            if (len2 > 1e-10f)
            {
                float len = Mathf.Sqrt(len2);
                Vector3 u = ab / len, w = p - a;
                float dd = Vector3.Dot(d, u), wu = Vector3.Dot(w, u);
                Vector3 wp = w - u * wu, dp = d - u * dd;
                float A = dp.sqrMagnitude, B = Vector3.Dot(wp, dp), C = wp.sqrMagnitude - r2;
                float lo = -float.MaxValue, hi = float.MaxValue;
                bool ok = true;
                if (A > 1e-8f)
                {
                    float disc = B * B - A * C;
                    if (disc < 0f) ok = false;
                    else { float sq = Mathf.Sqrt(disc); lo = (-B - sq) / A; hi = (-B + sq) / A; }
                }
                else if (C > 0f) ok = false;   // parallel to the axis and outside the tube
                if (ok)
                {
                    if (Mathf.Abs(dd) > 1e-6f)
                    {
                        float s0 = -wu / dd, s1 = (len - wu) / dd;   // the end planes
                        if (s0 > s1) { float x = s0; s0 = s1; s1 = x; }
                        if (s0 > lo) lo = s0;
                        if (s1 < hi) hi = s1;
                    }
                    else if (wu < 0f || wu > len) ok = false;
                    if (ok && hi >= lo && hi >= 0f && (!any || hi > tExit)) { tExit = hi; any = true; }
                }
            }
            return any;
        }

        // package C (0.7.9 reactions): wakes the soft helpers within `radius` of `point` with a velocity along `dir` (m/s, falling off with
        // distance); called before Simulate in the same frame. Uses only what the previous Simulate left (R = the world rest place, HasPrev,
        // _bOk), so it needs nothing of this frame. The velocity is world space like Helper.Vel; the spring brings the skin back and Simulate's
        // Clamp bounds the displacement. Speed is clamped to 1 m/s. No allocation. A no-op when not built.
        public void Kick(Vector3 point, Vector3 dir, float speed, float radius)
        {
            if (_bOk == null || _helpers.Count == 0 || !(radius > 1e-4f) || !(speed > 0f)) return;
            if (float.IsNaN(point.x) || float.IsNaN(dir.x) || float.IsNaN(speed) || float.IsNaN(radius)) return;
            float dm = dir.magnitude;
            if (!(dm > 1e-5f)) return;
            if (speed > 1f) speed = 1f;
            float r2 = radius * radius, k = speed / dm;
            for (int i = 0; i < _helpers.Count; i++)
            {
                var h = _helpers[i];
                if (!h.HasPrev || !_bOk[h.BoneIdx]) continue;
                float d2 = (h.R - point).sqrMagnitude;
                if (d2 >= r2) continue;
                h.Vel += dir * (k * (1f - Mathf.Sqrt(d2) / radius));
                h.Asleep = false;
            }
        }

        // Every frame after Body applies (bones carry our scales), inside the apply step: the soft points' springs and contacts.
        // softness 0..1 (stiff..soft), spring 0..1 (0 = settles at once and doesn't lag her motion). quality (0..2) is accepted so the
        // caller can pass its setting along; the build's own quality (Update) decides the points, so nothing here depends on it.
        // gravity 0..1 (0.7.3): how much each zone sags toward the floor. Every helper's spring rests at Sag = down * zoneSag * limit * gravity
        // (world space, see Helper.Sag) instead of at zero, so the skin hangs / flattens with her pose and the springiness oscillates around
        // it. gravity 0 is exactly the 0.7.2 behaviour (Sag is the zero vector everywhere, every Sag term vanishes).
        public void Simulate(float dt, List<SoftCapsule> caps, float softness, float spring, bool debug, int quality = 1, float gravity = 0f)
        {
            ImpactCount = 0;   // 0.8.0: this frame's impacts are filled by the contact loop below
            int nh = _helpers.Count;
            if (nh == 0 || _bL2W == null) { if (_dbgOn) DebugOff(); if (Contacts.Length > 0) Contacts = ""; return; }
            if (!(dt > 1e-5f)) return;   // paused
            if (dt > 0.1f) dt = 0.1f;
            softness = Mathf.Clamp01(softness); spring = Mathf.Clamp01(spring);
            float k = Mathf.Lerp(400f, 120f, softness);
            float zeta = Mathf.Lerp(1f, 0.45f, spring);
            float inert = Mathf.Lerp(0f, 0.5f, spring);
            float limit = Mathf.Lerp(0.02f, 0.045f, softness);
            gravity = Mathf.Clamp01(gravity);
            if (gravity < 0.001f) gravity = 0f;
            // 0.8.0 (Tissue.Zone): each zone has its own stiffness, depth limit and damping: breasts softest and bounciest, outer thighs firm and shallow
            for (int z = 0; z < _zSagV.Length; z++)
            {
                // 0.11.8: a flexed muscle under the zone (glute, quads / adductors, abs) dents less and pushes back sooner (MuscleFlex)
                _zK[z] = k * _zKMul[z]; _zLim[z] = limit * _zLimMul[z] * MuscleFlex.SoftFactor(Zones[z].Id);
                _zDamp[z] = 2f * zeta * Mathf.Sqrt(_zK[z]) * _zDampMul[z];
                // 0.11.37 (user: chubby glutes should move with collisions and physics like the breasts): the zone's fat (Chubby) makes it
                // softer (12 mm halves the spring: slower, wider jiggle), lets it give further under a hand or his body (20 mm doubles the
                // travel) and damps it less (it keeps wobbling); slimmer is firmer
                float fat = Tissue.ZoneFatMm(Zones[z].Id);
                if (fat != 0f)
                {
                    float soft = Mathf.Clamp(1f + fat / 12f, 0.7f, 3f);
                    _zK[z] /= soft;
                    _zLim[z] *= Mathf.Clamp(1f + fat / 20f, 0.75f, 2.5f);
                    _zDamp[z] = 2f * zeta * Mathf.Sqrt(_zK[z]) * _zDampMul[z] / Mathf.Clamp(1f + fat / 30f, 0.85f, 1.6f);
                }
                _zSagV[z] = gravity > 0f ? Vector3.down * (_zSag[z] * _zLim[z] * gravity) : Vector3.zero;
            }

            // the bones' matrices once (many helpers share a bone), and a destroyed bone silences its helpers
            for (int i = 0; i < _bones.Count; i++)
            {
                var bt = _bones[i];
                bool ok = bt != null;
                _bOk[i] = ok;
                if (!ok) continue;
                _bL2W[i] = bt.localToWorldMatrix;
                _bW2L[i] = bt.worldToLocalMatrix;
            }

            // the capsules in world space, with their velocity (the previous endpoints are kept; reset when the set changes)
            int nc = caps != null ? Math.Min(caps.Count, MaxCaps) : 0;
            EnsureCaps(nc);
            bool reset = VrHands.Version != _capVersion || nc != _cN;
            _capVersion = VrHands.Version; _cN = nc;
            float invDt = 1f / dt;
            if (_cPeak.Length < nc) { Array.Resize(ref _cPeak, nc); Array.Resize(ref _cAway, nc); }
            // her soft points' centroid (rest places, last frame's bones are fine for a velocity): the frame her motion is measured against
            Vector3 herV = Vector3.zero;
            {
                Vector3 c = Vector3.zero; int cn = 0;
                for (int i = 0; i < _helpers.Count; i++) { var hh = _helpers[i]; if (_bOk[hh.BoneIdx]) { c += hh.R; cn++; } }
                if (cn > 0)
                {
                    c /= cn;
                    if (_herCOk && !reset) herV = (c - _herCPrev) * invDt;
                    if (herV.sqrMagnitude > TeleportSpeed * TeleportSpeed) herV = Vector3.zero;
                    _herCPrev = c; _herC = c; _herCOk = true;
                }
            }
            float decay = Mathf.Exp(-dt / StrokeTau);
            for (int i = 0; i < nc; i++)
            {
                var cp = caps[i];
                _cHit[i] = false;
                if (cp.T == null || !(cp.Radius > 0f)) { _cR[i] = -1f; _cOk[i] = false; continue; }
                var m = cp.T.localToWorldMatrix;
                Vector3 a = m.MultiplyPoint3x4(cp.LocalA), b = m.MultiplyPoint3x4(cp.LocalB);
                Vector3 va = Vector3.zero, vb = Vector3.zero;
                if (!reset && _cOk[i])
                {
                    va = (a - _cPA[i]) * invDt; vb = (b - _cPB[i]) * invDt;
                    float sa = va.sqrMagnitude, sbb = vb.sqrMagnitude;
                    if (sa > TeleportSpeed * TeleportSpeed || sbb > TeleportSpeed * TeleportSpeed) { va = Vector3.zero; vb = Vector3.zero; }   // jumped, not moved
                    else
                    {
                        if (sa > MaxCapSpeed * MaxCapSpeed) va *= MaxCapSpeed / Mathf.Sqrt(sa);
                        if (sbb > MaxCapSpeed * MaxCapSpeed) vb *= MaxCapSpeed / Mathf.Sqrt(sbb);
                    }
                }
                if (reset || !_cOk[i])
                {
                    _cHand[i] = HandOf(cp);
                    _cSrc[i] = cp.Src;
                    _cLabel[i] = cp.Src != null ? null : cp.Owner + "/" + cp.Kind;
                    _cBody[i] = cp.Kind == "partnerBody";
                }
                // alive unless its source is hidden this frame; a dead capsule is still tracked so its speed is right when it comes back
                _cLive[i] = (cp.Src == null || cp.Src.Live) && (cp.Kind != "partnerBody" || PartnerBody);   // 0.8.0: his pelvis/thighs only with [Physics] PartnerBody
                int sid = cp.Src != null ? cp.Src.Id : 0;
                _cId[i] = sid > 0 && sid < VrHands.MaxSources ? sid : 0;
                _cPA[i] = a; _cPB[i] = b; _cOk[i] = true;
                _cA[i] = a; _cB[i] = b; _cVA[i] = va; _cVB[i] = vb;
                _cR[i] = cp.Radius;
                _cMid[i] = (a + b) * 0.5f;
                if (_cBody[i] && _herCOk)
                {
                    Vector3 to = _herC - _cMid[i];
                    float tl = to.magnitude;
                    float close = tl > 1e-4f ? Vector3.Dot((va + vb) * 0.5f - herV, to / tl) : 0f;
                    _cPeak[i] = Mathf.Max(_cPeak[i] * decay, close);
                    if (close < -StrokeBack) _cAway[i] = true;
                }
                else { _cPeak[i] = 0f; _cAway[i] = false; }
                float reach = (b - a).magnitude * 0.5f + cp.Radius + SkinMargin;
                _cReach2[i] = reach * reach;
                string kind = cp.Kind;
                _cCode[i] = kind == "thighL" ? 1 : kind == "thighR" ? 2 : 0;
            }

            // 1-2: where the rest place is now, and the lag behind her own motion
            for (int i = 0; i < nh; i++)
            {
                var h = _helpers[i];
                _hPrevSrc[i] = h.SrcMask;
                h.Touched = false; h.Bulged = false; h.Push = Vector3.zero; h.HandMask = 0; h.SrcMask = 0UL;
                if (!_bOk[h.BoneIdx]) continue;
                var m = _bL2W[h.BoneIdx];
                var r = m.MultiplyPoint3x4(h.SLocal);
                h.R = r;
                h.N = m.MultiplyVector(h.NLocal).normalized;
                // the sag: gravity's pull, but never further outward along the normal than SagOutward * limit (the clamp below leaves the
                // spring's equilibrium reachable: skin that rises more than that is not a sag any more)
                Vector3 sag = _zSagV[h.ZoneIdx];
                if (sag.x != 0f || sag.y != 0f || sag.z != 0f)
                {
                    float zl = _zLim[h.ZoneIdx];
                    float sn = Vector3.Dot(sag, h.N), lo = -SagOutward * zl;
                    if (sn < lo) { sag += h.N * (lo - sn); sn = lo; }
                    // the tangential part too: at most 0.5 limit, so with the clamp's own 0.5 limit of slide around it the total sideways stays <= limit
                    Vector3 stg = sag - h.N * sn;
                    float tm = stg.sqrMagnitude, tcap = 0.5f * zl;
                    if (tm > tcap * tcap) sag = h.N * sn + stg * (tcap / Mathf.Sqrt(tm));
                }
                h.Sag = sag;
                if (h.Asleep)
                {
                    // asleep means resting at its equilibrium: follow a Sag that drifted less than 0.1 mm, wake for a bigger change (pose change)
                    if ((h.Disp - sag).sqrMagnitude < 1e-8f) h.Disp = sag; else h.Asleep = false;
                }
                if (h.HasPrev)
                {
                    var d = r - h.PrevRest;
                    float d2 = d.sqrMagnitude;
                    if (d2 > 0.04f)   // moved 20 cm in a frame: an animation cut or teleport, not motion to lag behind
                    {
                        if (!h.Asleep || (h.Disp - sag).sqrMagnitude > 0f) { h.Disp = sag; h.Vel = Vector3.zero; h.Asleep = false; }
                    }
                    else if (inert > 0f && d2 > 1e-10f) { h.Disp -= d * inert; h.Asleep = false; }
                }
                h.PrevRest = r; h.HasPrev = true;
            }

            // 3: the springs, in fixed substeps
            _acc += dt;
            int steps = 0;
            while (_acc >= Step && steps < MaxSteps)
            {
                for (int i = 0; i < nh; i++)
                {
                    var h = _helpers[i];
                    if (h.Asleep) continue;
                    int zi = h.ZoneIdx;
                    h.Vel += ((h.Disp - h.Sag) * -_zK[zi] - h.Vel * _zDamp[zi]) * Step;
                    h.Disp += h.Vel * Step;
                }
                _acc -= Step; steps++;
            }
            if (_acc > Step) _acc = 0f;   // fell behind (hitch): drop the rest rather than spiral

            // 4: contacts, the skin point is pushed out of every capsule that reaches it
            for (int i = 0; i < nh; i++)
            {
                var h = _helpers[i];
                if (!_bOk[h.BoneIdx]) continue;
                Vector3 before = h.Disp;
                for (int ci = 0; ci < nc; ci++)
                {
                    float cr = _cR[ci];
                    if (cr < 0f || !_cLive[ci]) continue;
                    int code = _cCode[ci];
                    if (code == 1 && !(h.Zone.InnerThigh && h.Zone.Side < 0)) continue;   // a thigh presses the other one's inner side only
                    if (code == 2 && !(h.Zone.InnerThigh && h.Zone.Side > 0)) continue;
                    Vector3 p = h.R + h.Disp;
                    var dm = p - _cMid[ci];
                    if (dm.sqrMagnitude > _cReach2[ci]) continue;
                    Vector3 a = _cA[ci], ab = _cB[ci] - a;
                    float len2 = ab.sqrMagnitude;
                    float t = len2 > 1e-10f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2) : 0f;
                    Vector3 closest = a + ab * t;
                    Vector3 u = p - closest;
                    float reach = cr + SkinMargin;
                    float d2 = u.sqrMagnitude;
                    if (d2 >= reach * reach) continue;
                    // Imprint: the point goes along its inward normal to where that line leaves the capsule, so the dent has the capsule's profile.
                    // No exit found (numerically on the axis, or the normal is degenerate): the old radial push out of the capsule.
                    float te;
                    Vector3 np;
                    if (RayExit(p, h.N, a, ab, len2, reach, out te)) np = p + h.N * (te + 1e-4f);
                    else
                    {
                        float dist = Mathf.Sqrt(d2);
                        np = closest + (dist > 1e-5f ? u / dist : h.N) * reach;
                    }
                    h.Disp = np - h.R;
                    // the capsule's own outward direction at the new place: no velocity into it; at most 60% of its speed along it (set, not summed)
                    Vector3 un = np - (a + ab * (len2 > 1e-10f ? Mathf.Clamp01(Vector3.Dot(np - a, ab) / len2) : 0f));
                    float unm = un.magnitude;
                    Vector3 dir = unm > 1e-5f ? un / unm : h.N;
                    float vn = Vector3.Dot(h.Vel, dir);
                    if (vn < 0f) { h.Vel -= dir * vn; vn = 0f; }
                    float vc = Vector3.Dot(_cVA[ci] + (_cVB[ci] - _cVA[ci]) * t, dir) * 0.6f;
                    if (vc > vn) h.Vel += dir * (vc - vn);
                    if (_cHand[ci] != 0) h.HandMask |= _cHand[ci];
                    h.SrcMask |= 1UL << _cId[ci];
                    if (_cId[ci] == 0) _noSrcLabel = _cLabel[ci] ?? "";
                    h.Touched = true; h.Asleep = false; h.LastCap = ci;
                    _cHit[ci] = true;
                    Vector3 capVel = _cVA[ci] + (_cVB[ci] - _cVA[ci]) * t;
                    float approach = Vector3.Dot(capVel, h.N);
                    if (ThrustRipple && _cBody[ci])
                    {
                        if (_cAway[ci] && _cPeak[ci] >= ThrustMinSpeed && NoteImpact(h, i, ci, Mathf.Max(approach, _cPeak[ci]), capVel, np, true))
                            for (int kb = 0; kb < _cN; kb++) if (_cBody[kb] && _cId[kb] == _cId[ci]) _cAway[kb] = false;   // one stroke: his pelvis and thighs disarm together
                    }
                    else if (approach >= ImpactMinSpeed) NoteImpact(h, i, ci, approach, capVel, np, false);
                }
                if (h.Touched) h.Push = h.Disp - before;
            }

            // 5: what skin can do: sink in up to the limit, rise a little, slide less
            for (int i = 0; i < nh; i++)
            {
                var h = _helpers[i];
                if (!h.Asleep) Clamp(h, _zLim[h.ZoneIdx]);
            }

            // 5b: how deep each physics hand is into her (the deepest touched point's inward dent, as a share of the limit), smoothed 0.1 s
            // 0.8.0: the resistance a hand feels is Tissue.Resist of the part's own limit: soft parts let it sink first, firm ones push back sooner
            float dentL = 0f, dentR = 0f, resL = 0f, resR = 0f;
            for (int i = 0; i < nh; i++)
            {
                var h = _helpers[i];
                if (!h.Touched || h.HandMask == 0) continue;
                float zl = _zLim[h.ZoneIdx];
                float dn = Vector3.Dot(h.Disp - h.Sag, h.N);   // what the hand pushed, not the sag under gravity
                if (dn > zl) dn = zl;
                float rs = Tissue.Resist(dn, zl);
                if ((h.HandMask & 1) != 0) { if (dn > dentL) dentL = dn; if (rs > resL) resL = rs; }
                if ((h.HandMask & 2) != 0) { if (dn > dentR) dentR = dn; if (rs > resR) resR = rs; }
            }
            {
                float ka = 1f - Mathf.Exp(-dt / 0.1f);
                _rL += (resL - _rL) * ka;
                _rR += (resR - _rR) * ka;
                LeftResist = _rL; RightResist = _rR; _ownsResist = true;
                if (_rL > _peakRL) _peakRL = _rL;
                if (_rR > _peakRR) _peakRR = _rR;
                if (dentL > _peakDL) _peakDL = dentL;
                if (dentR > _peakDR) _peakDR = dentR;
            }

            // 6: a hit drags the zone's other points along, fading with distance
            for (int i = 0; i < nh; i++)
            {
                var h = _helpers[i];
                if (!h.Touched || h.Push.sqrMagnitude < 1e-10f) continue;
                float s2 = _zSigma[h.ZoneIdx]; s2 *= s2;
                float bulge = _zBulge[h.ZoneIdx];
                for (int j = _zH0[h.ZoneIdx]; j < _zH1[h.ZoneIdx]; j++)
                {
                    var o = _helpers[j];
                    if (o.Point == h.Point || !_bOk[o.BoneIdx]) continue;
                    var dr = o.R - h.R;
                    o.Disp += h.Push * (bulge * Mathf.Exp(-dr.sqrMagnitude / s2));
                    o.Asleep = false; o.Bulged = true;
                }
            }
            for (int i = 0; i < nh; i++)
            {
                var h = _helpers[i];
                if (h.Bulged) Clamp(h, _zLim[h.ZoneIdx]);
            }

            // 7: write S, count the contacts
            _stamp++;
            _nContacts = 0;
            for (int z = 0; z < _zCnt.Length; z++) _zCnt[z] = 0;
            for (int s = 0; s < _srcCnt.Length; s++) { _srcCnt[s] = 0; _srcOf[s] = null; }
            for (int i = 0; i < nc; i++) if (_cSrc[i] != null) _srcOf[_cId[i]] = _cSrc[i];
            for (int i = 0; i < nh; i++)
            {
                var h = _helpers[i];
                if (!_bOk[h.BoneIdx]) continue;
                if (h.Touched)
                {
                    var pt = h.Point;
                    if (pt.Stamp != _stamp)
                    {
                        pt.Stamp = _stamp; pt.SrcMask = 0UL;
                        _zCnt[h.ZoneIdx]++; _nContacts++;
                        float depth = h.Disp.magnitude;
                        if (depth > _peak) _peak = depth;
                    }
                    // per source: a point counts once for each source that touched it, whichever of its helpers it was
                    ulong fresh = h.SrcMask & ~pt.SrcMask;
                    if (fresh != 0UL)
                    {
                        pt.SrcMask |= fresh;
                        for (int id = 0; fresh != 0UL; id++, fresh >>= 1)
                        {
                            if ((fresh & 1UL) == 0UL) continue;
                            _srcCnt[id]++;
                            int zi = h.ZoneIdx * VrHands.MaxSources + id;
                            if (!_zSeen[zi]) { _zSeen[zi] = true; LogFirst(h, id); }
                        }
                    }
                }
                bool sagging = h.Sag.x != 0f || h.Sag.y != 0f || h.Sag.z != 0f;
                if (h.Asleep)
                {
                    if (sagging) h.S.localPosition = _bW2L[h.BoneIdx].MultiplyPoint3x4(h.R + h.Disp);   // the bone may have turned: Sag is a world offset
                    continue;
                }
                if (!h.Touched && (h.Disp - h.Sag).sqrMagnitude < 1e-8f && h.Vel.sqrMagnitude < 1e-6f)
                {
                    h.Disp = h.Sag; h.Vel = Vector3.zero; h.Asleep = true;
                    if (!sagging) { h.S.localPosition = h.SLocal; continue; }   // exactly its rest place, not a round trip through world space
                }
                h.S.localPosition = _bW2L[h.BoneIdx].MultiplyPoint3x4(h.R + h.Disp);
            }

            Report();
            if (debug) DebugDraw(nc); else if (_dbgOn) DebugOff();
        }

        // 0.8.0: a capsule touching a helper at `approach` m/s (along the skin's inward normal) is an impact when it is a fresh contact: its source was
        // not on this helper last frame, or the zone's last impact is 0.3 s old and this one is 1.5x faster; and the source made none in 0.3 s. Her own
        // capsules never. The four fastest of the frame are kept in Impacts (no allocation).
        bool NoteImpact(Helper h, int hi, int ci, float approach, Vector3 capVel, Vector3 at, bool thrust)
        {
            var src = _cSrc[ci];
            if (src != null && src.Kind == "hers") return false;
            int id = _cId[ci], z = h.ZoneIdx;
            float now = Time.time;
            bool fresh = (_hPrevSrc[hi] & (1UL << id)) == 0UL;
            if (!fresh && now - _zLastImpact[z] >= ImpactGap && approach >= 1.5f * _zLastApproach[z]) fresh = true;
            if (thrust) fresh = now - _srcLastImpact[id] >= ThrustGap;   // a stroke of his hips (armed by the caller: he backed off since the last one)
            if (!fresh || now - _srcLastImpact[id] < (thrust ? ThrustGap : ImpactGap)) return false;
            int slot;
            if (ImpactCount < Impacts.Length) slot = ImpactCount++;
            else
            {
                slot = 0;
                for (int k = 1; k < Impacts.Length; k++) if (Impacts[k].Speed < Impacts[slot].Speed) slot = k;
                if (approach <= Impacts[slot].Speed) return false;
            }
            Impacts[slot] = new SoftImpact { P = at, Dir = capVel.normalized, Speed = approach, Bone = h.Bone, Zone = z, SrcId = id, By = src != null ? src.Name : _cLabel[ci], Thrust = thrust };
            _zLastImpact[z] = now; _zLastApproach[z] = approach; _srcLastImpact[id] = now;
            return true;
        }

        // sink in (along the inward normal) up to limit, rise up to 0.3 limit, slide up to 0.5 limit. With a sag the same room is left around the
        // equilibrium: the slide is limited relative to the sag's tangential part, and the rise to 0.3 limit or the sag's own outward normal
        // part if that is more (Simulate caps that at SagOutward * limit). Sag zero: exactly the 0.7.2 clamp.
        static void Clamp(Helper h, float limit)
        {
            if (h.Sag.x != 0f || h.Sag.y != 0f || h.Sag.z != 0f)
            {
                float sn = Vector3.Dot(h.Sag, h.N);
                Vector3 st = h.Sag - h.N * sn;
                float dn2 = Vector3.Dot(h.Disp, h.N);
                Vector3 tan2 = h.Disp - h.N * dn2 - st;
                float tl2 = 0.5f * limit, tm22 = tan2.sqrMagnitude;
                if (tm22 > tl2 * tl2) tan2 *= tl2 / Mathf.Sqrt(tm22);
                float lo = Mathf.Min(-0.3f * limit, sn);
                if (dn2 > limit) dn2 = limit; else if (dn2 < lo) dn2 = lo;
                h.Disp = st + tan2 + h.N * dn2;
                return;
            }
            float dn = Vector3.Dot(h.Disp, h.N);
            Vector3 tan = h.Disp - h.N * dn;
            float tl = 0.5f * limit, tm2 = tan.sqrMagnitude;
            if (tm2 > tl * tl) tan *= tl / Mathf.Sqrt(tm2);
            if (dn > limit) dn = limit; else if (dn < -0.3f * limit) dn = -0.3f * limit;
            h.Disp = tan + h.N * dn;
        }

        // ---- status and log ----

        // once per (zone, source) per build: who touched which zone first, and how far the point was displaced
        void LogFirst(Helper h, int id)
        {
            string by = id > 0 && _srcOf[id] != null ? _srcOf[id].Name + " (" + _srcOf[id].Kind + ")" : _noSrcLabel;
            Plugin.Log.LogInfo("Soft zones: first contact in " + h.Zone.Id + " by " + by + ", " + h.Disp.magnitude.ToString("0.0##") + " m displaced");
        }

        // "contacts: 3 (glute_L 2, thighIn_R 1) by your right hand 2, Player 1" (top 3 sources by touched points), rebuilt only when it changes;
        // a summary line to the log every 10 s while contacts happen (it carries the same text, so "by ..." is in it too).
        int _t0, _t1, _t2;   // the top 3 source ids this frame (-1 = none)
        void Report()
        {
            int c0 = 0, c1 = 0, c2 = 0;
            _t0 = _t1 = _t2 = -1;
            for (int s = 0; s < _srcCnt.Length; s++)
            {
                int c = _srcCnt[s];
                if (c <= 0) continue;
                if (c > c0) { c2 = c1; _t2 = _t1; c1 = c0; _t1 = _t0; c0 = c; _t0 = s; }
                else if (c > c1) { c2 = c1; _t2 = _t1; c1 = c; _t1 = s; }
                else if (c > c2) { c2 = c; _t2 = s; }
            }
            long sig = 0;
            for (int z = 0; z < _zCnt.Length; z++) sig = sig * 131 + _zCnt[z];
            sig = sig * 131 + _t0; sig = sig * 131 + c0; sig = sig * 131 + _t1; sig = sig * 131 + c1; sig = sig * 131 + _t2; sig = sig * 131 + c2;
            if (sig != _sig)
            {
                _sig = sig;
                if (_nContacts == 0) Contacts = "contacts: 0";
                else
                {
                    _sb.Length = 0;
                    _sb.Append("contacts: ").Append(_nContacts).Append(" (");
                    bool first = true;
                    for (int z = 0; z < _zCnt.Length; z++)
                    {
                        if (_zCnt[z] == 0) continue;
                        if (!first) _sb.Append(", ");
                        _sb.Append(Zones[z].Id).Append(' ').Append(_zCnt[z]);
                        first = false;
                    }
                    _sb.Append(')');
                    if (_t0 >= 0)
                    {
                        _sb.Append(" by ");
                        AppendSource(_t0, c0);
                        if (_t1 >= 0) { _sb.Append(", "); AppendSource(_t1, c1); }
                        if (_t2 >= 0) { _sb.Append(", "); AppendSource(_t2, c2); }
                    }
                    Contacts = _sb.ToString();
                }
            }
            if (_nContacts > 0 && Time.unscaledTime >= _nextSummary)
            {
                _nextSummary = Time.unscaledTime + 10f;
                Plugin.Log.LogInfo("Soft zones: " + Contacts + ", deepest " + (_peak * 100f).ToString("0.0") + " cm since the last line; physics hands: dent L "
                                   + (_peakDL * 100f).ToString("0.0") + " cm / R " + (_peakDR * 100f).ToString("0.0") + " cm, resist L " + _peakRL.ToString("0.00") + " / R " + _peakRR.ToString("0.00"));
                _peak = 0f; _peakRL = _peakRR = _peakDL = _peakDR = 0f;
            }
        }

        // a source's name and its touched-point count; id 0 = capsules without a source, named by their Owner/Kind
        void AppendSource(int id, int count)
        {
            var src = _srcOf[id];
            _sb.Append(id > 0 && src != null ? src.Name : _noSrcLabel.Length > 0 ? _noSrcLabel : "other").Append(' ').Append(count);
        }

        // ---- debug markers: a sphere per point (green at rest, red touched), a capsule per contact capsule ----

        class Marker { public GameObject Go; public Transform Tr; public Renderer R; public int State; }   // State: 0 green, 1 red (hit), 2 grey (dead capsule)

        bool _dbgOn;
        GameObject _dbgRoot;
        Material _matRest, _matHit, _matDead;
        readonly List<Marker> _dbgPts = new List<Marker>();
        readonly List<Marker> _dbgCaps = new List<Marker>();
        static readonly string[] ColorProps = { "_UnlitColor", "_BaseColor", "_Color" };

        static void Tint(Material m, Color c)
        {
            for (int i = 0; i < ColorProps.Length; i++) if (m.HasProperty(ColorProps[i])) m.SetColor(ColorProps[i], c);
        }

        Material MakeMaterial(Color c)
        {
            var sh = Shader.Find("HDRP/Unlit");
            if (sh == null) sh = Shader.Find("HDRP/Lit");
            if (sh == null) return null;
            var m = new Material(sh) { name = Prefix + "_debug" };
            Tint(m, c);
            return m;
        }

        Marker NewMarker(PrimitiveType type, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = Prefix + "_debug";
            foreach (var comp in go.GetComponents<Component>())   // markers must never be touched or touch anything
                if (comp != null && comp.GetType().Name.EndsWith("Collider", StringComparison.Ordinal)) UnityEngine.Object.DestroyImmediate(comp);
            go.transform.SetParent(_dbgRoot.transform, false);
            var r = go.GetComponent<Renderer>();
            if (r != null)
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                if (mat != null) r.sharedMaterial = mat;
            }
            return new Marker { Go = go, Tr = go.transform, R = r, State = 0 };
        }

        static void Paint(Marker mk, int state, Material rest, Material hit, Material dead)
        {
            if (mk.State == state || mk.R == null) return;
            var m = state == 2 ? dead : state == 1 ? hit : rest;
            if (m != null) mk.R.sharedMaterial = m;
            mk.State = state;
        }

        void DebugDraw(int nc)
        {
            if (!_dbgOn)
            {
                _dbgOn = true;
                _dbgRoot = new GameObject(Prefix + "_debug_markers");
                _matRest = MakeMaterial(new Color(0.1f, 0.9f, 0.2f, 1f));
                _matHit = MakeMaterial(new Color(1f, 0.1f, 0.1f, 1f));
                _matDead = MakeMaterial(new Color(0.5f, 0.5f, 0.5f, 1f));   // 0.7.7: a capsule whose source is hidden touches nothing
            }
            while (_dbgPts.Count < _pts.Count) _dbgPts.Add(null);
            for (int i = 0; i < _pts.Count; i++)
            {
                var pt = _pts[i];
                if (pt.Main < 0) continue;
                var h = _helpers[pt.Main];
                if (!_bOk[h.BoneIdx]) continue;
                var mk = _dbgPts[i];
                if (mk == null || mk.Go == null)
                {
                    mk = NewMarker(PrimitiveType.Sphere, _matRest);
                    mk.Tr.localScale = Vector3.one * 0.016f;
                    _dbgPts[i] = mk;
                }
                mk.Tr.position = h.R + h.Disp;
                Paint(mk, pt.Stamp == _stamp ? 1 : 0, _matRest, _matHit, _matDead);
            }
            while (_dbgCaps.Count < nc) _dbgCaps.Add(null);
            for (int i = 0; i < _dbgCaps.Count; i++)
            {
                var mk = _dbgCaps[i];
                if (i >= nc || _cR[i] < 0f) { if (mk != null && mk.Go != null && mk.Go.activeSelf) mk.Go.SetActive(false); continue; }
                if (mk == null || mk.Go == null)
                {
                    mk = NewMarker(PrimitiveType.Capsule, _matRest);
                    _dbgCaps[i] = mk;
                }
                if (!mk.Go.activeSelf) mk.Go.SetActive(true);
                Vector3 a = _cA[i], b = _cB[i], ab = b - a;
                float len = ab.magnitude, r = _cR[i];
                mk.Tr.position = _cMid[i];
                mk.Tr.rotation = len > 1e-5f ? Quaternion.FromToRotation(Vector3.up, ab / len) : Quaternion.identity;
                mk.Tr.localScale = new Vector3(2f * r, (len + 2f * r) * 0.5f, 2f * r);   // the primitive is 1 m wide and 2 m tall
                Paint(mk, !_cLive[i] ? 2 : _cHit[i] ? 1 : 0, _matRest, _matHit, _matDead);
            }
        }

        void DebugOff()
        {
            _dbgPts.Clear(); _dbgCaps.Clear();
            if (_dbgRoot != null) UnityEngine.Object.Destroy(_dbgRoot);
            if (_matRest != null) UnityEngine.Object.Destroy(_matRest);
            if (_matHit != null) UnityEngine.Object.Destroy(_matHit);
            if (_matDead != null) UnityEngine.Object.Destroy(_matDead);
            _dbgRoot = null; _matRest = _matHit = _matDead = null;
            _dbgOn = false;
        }
    }
}
