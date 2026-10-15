using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // The soft zones' simulation (see SoftZones.cs for what the points and helper bones are). Every frame after Body applies:
    // each helper's rest place R follows its bone, a spring holds the displacement Disp (world space) to it, contact
    // capsules (VrHands) push the point out of themselves and hand it some of their speed, the displacement is clamped
    // to what skin can do, a hit spreads a little to the zone's other points, and S is written at R + Disp. No allocations
    // per frame: state lives in preallocated arrays and fields; strings are rebuilt only when the contacts change.
    internal partial class SoftZones
    {
        const float Step = 1f / 120f;                  // fixed substep: the springs behave the same at any frame rate
        const int MaxSteps = 4;
        const float PointRadius = 0.012f, Margin = 0.008f;   // the skin point's own size and the give before visible touch
        const int MaxCaps = 256;
        const float MaxCapSpeed = 3f, TeleportSpeed = 8f;    // m/s: faster capsules are clamped, much faster ones jumped

        // ---- state of the current build ----

        Matrix4x4[] _bL2W, _bW2L;      // per distinct bone, this frame
        bool[] _bOk;
        int[] _zH0, _zH1;              // per zone: its helpers [H0, H1) in _helpers (they are built zone by zone), -1 = none
        float[] _zSigma;               // per zone: spacing of its points (m), the reach of a hit
        float _acc;                    // fixed-step accumulator

        // ---- the capsules this frame ----

        Vector3[] _cA = new Vector3[0], _cB = new Vector3[0], _cPA = new Vector3[0], _cPB = new Vector3[0], _cVA = new Vector3[0], _cVB = new Vector3[0], _cMid = new Vector3[0];
        float[] _cR = new float[0], _cReach2 = new float[0];
        int[] _cCode = new int[0];     // 0 any zone, 1 her left thigh (only the right inner thigh feels it), 2 her right thigh
        bool[] _cOk = new bool[0], _cHit = new bool[0];
        int _cN, _capVersion = -1;

        // ---- contacts ----

        int[] _zCnt;                   // per zone: points touched this frame
        bool[] _zSeen;                 // per zone: first contact logged
        int _stamp, _nContacts;
        long _sig = -1;
        float _nextSummary, _peak;
        readonly StringBuilder _sb = new StringBuilder();
        public string Contacts = "";

        void SimBuilt(int[] zoneH0, int[] zoneH1)
        {
            _zH0 = zoneH0; _zH1 = zoneH1;
            _zSigma = new float[Zones.Length]; _zCnt = new int[Zones.Length]; _zSeen = new bool[Zones.Length];
            var pts = new int[Zones.Length];
            foreach (var p in _pts) pts[p.ZoneIdx]++;
            for (int z = 0; z < Zones.Length; z++)
            {
                int ring = pts[z] - 1;   // the ring points around the centre one
                _zSigma[z] = Mathf.Max(0.02f, ring >= 2 ? 0.55f * Zones[z].Radius * 2f * Mathf.Sin(Mathf.PI / ring) : 0.55f * Zones[z].Radius);
            }
            _bL2W = new Matrix4x4[_bones.Count]; _bW2L = new Matrix4x4[_bones.Count]; _bOk = new bool[_bones.Count];
            _acc = 0f; _capVersion = -1; _nContacts = 0; _sig = -1; _stamp = 0; _peak = 0f;
        }

        void SimReset()
        {
            DebugOff();
            _bL2W = _bW2L = null; _bOk = null; _zH0 = _zH1 = null; _zSigma = null; _zCnt = null; _zSeen = null;
            _nContacts = 0; _sig = -1;
        }

        void EnsureCaps(int n)
        {
            if (_cA.Length >= n) return;
            int m = Math.Max(n, Math.Max(16, _cA.Length * 2));
            _cA = new Vector3[m]; _cB = new Vector3[m]; _cPA = new Vector3[m]; _cPB = new Vector3[m]; _cVA = new Vector3[m]; _cVB = new Vector3[m]; _cMid = new Vector3[m];
            _cR = new float[m]; _cReach2 = new float[m]; _cCode = new int[m]; _cOk = new bool[m]; _cHit = new bool[m];
            _capVersion = -1;   // the old previous positions are gone
        }

        // Every frame after Body applies (bones carry our scales), inside the apply step: the soft points' springs and contacts.
        // softness 0..1 (stiff..soft), spring 0..1 (0 = settles at once and doesn't lag her motion).
        public void Simulate(float dt, List<SoftCapsule> caps, float softness, float spring, bool debug, int quality = 1)
        {
            int nh = _helpers.Count;
            if (nh == 0 || _bL2W == null) { if (_dbgOn) DebugOff(); if (Contacts.Length > 0) Contacts = ""; return; }
            if (!(dt > 1e-5f)) return;   // paused
            if (dt > 0.1f) dt = 0.1f;
            softness = Mathf.Clamp01(softness); spring = Mathf.Clamp01(spring);
            float k = Mathf.Lerp(400f, 120f, softness);
            float zeta = Mathf.Lerp(1f, 0.45f, spring);
            float damp = 2f * zeta * Mathf.Sqrt(k);
            float inert = Mathf.Lerp(0f, 0.5f, spring);
            float limit = Mathf.Lerp(0.02f, 0.045f, softness);

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
                _cPA[i] = a; _cPB[i] = b; _cOk[i] = true;
                _cA[i] = a; _cB[i] = b; _cVA[i] = va; _cVB[i] = vb;
                _cR[i] = cp.Radius;
                _cMid[i] = (a + b) * 0.5f;
                float reach = (b - a).magnitude * 0.5f + cp.Radius + PointRadius + Margin;
                _cReach2[i] = reach * reach;
                string kind = cp.Kind;
                _cCode[i] = kind == "thighL" ? 1 : kind == "thighR" ? 2 : 0;
            }

            // 1-2: where the rest place is now, and the lag behind her own motion
            for (int i = 0; i < nh; i++)
            {
                var h = _helpers[i];
                h.Touched = false; h.Bulged = false; h.Push = Vector3.zero;
                if (!_bOk[h.BoneIdx]) continue;
                var m = _bL2W[h.BoneIdx];
                var r = m.MultiplyPoint3x4(h.SLocal);
                h.R = r;
                h.N = m.MultiplyVector(h.NLocal).normalized;
                if (h.HasPrev)
                {
                    var d = r - h.PrevRest;
                    float d2 = d.sqrMagnitude;
                    if (d2 > 0.04f)   // moved 20 cm in a frame: an animation cut or teleport, not motion to lag behind
                    {
                        if (!h.Asleep || h.Disp.sqrMagnitude > 0f) { h.Disp = Vector3.zero; h.Vel = Vector3.zero; h.Asleep = false; }
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
                    h.Vel += (h.Disp * -k - h.Vel * damp) * Step;
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
                    if (cr < 0f) continue;
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
                    float reach = cr + PointRadius + Margin;
                    float d2 = u.sqrMagnitude;
                    if (d2 >= reach * reach) continue;
                    float dist = Mathf.Sqrt(d2);
                    Vector3 dir = dist > 1e-5f ? u / dist : h.N;   // from the capsule to the point; inside its axis: into the body
                    h.Disp = closest + dir * reach - h.R;
                    // no velocity into the capsule; at most 60% of the capsule's own speed along the push (set, not summed, so it can't pile up)
                    float vn = Vector3.Dot(h.Vel, dir);
                    if (vn < 0f) { h.Vel -= dir * vn; vn = 0f; }
                    float vc = Vector3.Dot(_cVA[ci] + (_cVB[ci] - _cVA[ci]) * t, dir) * 0.6f;
                    if (vc > vn) h.Vel += dir * (vc - vn);
                    h.Touched = true; h.Asleep = false; h.LastCap = ci;
                    _cHit[ci] = true;
                }
                if (h.Touched) h.Push = h.Disp - before;
            }

            // 5: what skin can do: sink in up to the limit, rise a little, slide less
            for (int i = 0; i < nh; i++)
            {
                var h = _helpers[i];
                if (!h.Asleep) Clamp(h, limit);
            }

            // 6: a hit drags the zone's other points along, fading with distance
            for (int i = 0; i < nh; i++)
            {
                var h = _helpers[i];
                if (!h.Touched || h.Push.sqrMagnitude < 1e-10f) continue;
                float s2 = _zSigma[h.ZoneIdx]; s2 *= s2;
                for (int j = _zH0[h.ZoneIdx]; j < _zH1[h.ZoneIdx]; j++)
                {
                    var o = _helpers[j];
                    if (o.Point == h.Point || !_bOk[o.BoneIdx]) continue;
                    var dr = o.R - h.R;
                    o.Disp += h.Push * (0.25f * Mathf.Exp(-dr.sqrMagnitude / s2));
                    o.Asleep = false; o.Bulged = true;
                }
            }
            for (int i = 0; i < nh; i++)
            {
                var h = _helpers[i];
                if (h.Bulged) Clamp(h, limit);
            }

            // 7: write S, count the contacts
            _stamp++;
            _nContacts = 0;
            for (int z = 0; z < _zCnt.Length; z++) _zCnt[z] = 0;
            for (int i = 0; i < nh; i++)
            {
                var h = _helpers[i];
                if (!_bOk[h.BoneIdx]) continue;
                if (h.Touched && h.Point.Stamp != _stamp)
                {
                    h.Point.Stamp = _stamp;
                    _zCnt[h.ZoneIdx]++; _nContacts++;
                    float depth = h.Disp.magnitude;
                    if (depth > _peak) _peak = depth;
                    if (!_zSeen[h.ZoneIdx]) { _zSeen[h.ZoneIdx] = true; LogFirst(h, caps, nc); }
                }
                if (h.Asleep) continue;
                if (!h.Touched && h.Disp.sqrMagnitude < 1e-8f && h.Vel.sqrMagnitude < 1e-6f)
                {
                    h.Disp = Vector3.zero; h.Vel = Vector3.zero; h.Asleep = true;
                    h.S.localPosition = h.SLocal;   // exactly its rest place, not a round trip through world space
                    continue;
                }
                h.S.localPosition = _bW2L[h.BoneIdx].MultiplyPoint3x4(h.R + h.Disp);
            }

            Report();
            if (debug) DebugDraw(nc); else if (_dbgOn) DebugOff();
        }

        // sink in (along the inward normal) up to limit, rise up to 0.3 limit, slide up to 0.5 limit
        static void Clamp(Helper h, float limit)
        {
            float dn = Vector3.Dot(h.Disp, h.N);
            Vector3 tan = h.Disp - h.N * dn;
            float tl = 0.5f * limit, tm2 = tan.sqrMagnitude;
            if (tm2 > tl * tl) tan *= tl / Mathf.Sqrt(tm2);
            if (dn > limit) dn = limit; else if (dn < -0.3f * limit) dn = -0.3f * limit;
            h.Disp = tan + h.N * dn;
        }

        // ---- status and log ----

        void LogFirst(Helper h, List<SoftCapsule> caps, int nc)
        {
            string by = "";
            if (caps != null && h.LastCap >= 0 && h.LastCap < nc) { var c = caps[h.LastCap]; by = " by " + c.Owner + "/" + c.Kind; }
            Plugin.Log.LogInfo("Soft zones: first contact in " + h.Zone.Id + by + ", " + h.Disp.magnitude.ToString("0.0##") + " m displaced");
        }

        // "contacts: 3 (glute_L 2, thighIn_R 1)", rebuilt only when it changes; a summary line to the log every 10 s while contacts happen.
        void Report()
        {
            long sig = 0;
            for (int z = 0; z < _zCnt.Length; z++) sig = sig * 131 + _zCnt[z];
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
                    Contacts = _sb.Append(')').ToString();
                }
            }
            if (_nContacts > 0 && Time.unscaledTime >= _nextSummary)
            {
                _nextSummary = Time.unscaledTime + 10f;
                Plugin.Log.LogInfo("Soft zones: " + Contacts + ", deepest " + (_peak * 100f).ToString("0.0") + " cm since the last line");
                _peak = 0f;
            }
        }

        // ---- debug markers: a sphere per point (green at rest, red touched), a capsule per contact capsule ----

        class Marker { public GameObject Go; public Transform Tr; public Renderer R; public bool Red; }

        bool _dbgOn;
        GameObject _dbgRoot;
        Material _matRest, _matHit;
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
            return new Marker { Go = go, Tr = go.transform, R = r, Red = false };
        }

        static void Paint(Marker mk, bool red, Material rest, Material hit)
        {
            if (mk.Red == red || mk.R == null) return;
            var m = red ? hit : rest;
            if (m != null) mk.R.sharedMaterial = m;
            mk.Red = red;
        }

        void DebugDraw(int nc)
        {
            if (!_dbgOn)
            {
                _dbgOn = true;
                _dbgRoot = new GameObject(Prefix + "_debug_markers");
                _matRest = MakeMaterial(new Color(0.1f, 0.9f, 0.2f, 1f));
                _matHit = MakeMaterial(new Color(1f, 0.1f, 0.1f, 1f));
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
                Paint(mk, pt.Stamp == _stamp, _matRest, _matHit);
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
                Paint(mk, _cHit[i], _matRest, _matHit);
            }
        }

        void DebugOff()
        {
            _dbgPts.Clear(); _dbgCaps.Clear();
            if (_dbgRoot != null) UnityEngine.Object.Destroy(_dbgRoot);
            if (_matRest != null) UnityEngine.Object.Destroy(_matRest);
            if (_matHit != null) UnityEngine.Object.Destroy(_matHit);
            _dbgRoot = null; _matRest = _matHit = null;
            _dbgOn = false;
        }
    }
}
