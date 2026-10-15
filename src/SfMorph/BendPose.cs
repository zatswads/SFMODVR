using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // 0.8.0 bend correctives (design 2.2): her elbow, knee and hip angles vs the bind pose -> 9 bulge amplitudes (metres) that GpuContact's
    // Compose kernel adds per vertex by ContactBake.Bend weights. 9 slots (uniforms _BendAmt0..2): 0/1 biceps L/R (elbow), 2/3 thigh back L/R
    // (knee), 4/5 thigh top L/R (hip flexion), 6/7 calf L/R (knee), 8 lower-belly fold (both hips flexed; its vertices carry a negative weight).
    // Amplitudes are >= 0 metres, already x BendAmount; the per-vertex weight's sign says bulge out (+) or fold in (-).
    // A joint's angle = how far the child segment (C -> E) has turned from its bind-pose direction, both in the parent bone's (P) frame:
    // elbow P Shoulder C Elbow E Wrist, knee P Hip C Knee E Ankle, hip P Root_M C Hip E Knee.
    internal static class BendPose
    {
        public const int Slots = 9;
        public static readonly float[] Amt = new float[Slots];
        public static bool Any;
        public static string Status = "";
        // Joints with all three bones found and a bind pose to take the rest direction from (0..6), for GpuContact's status line.
        public static int Joints;

        // joints: 0/1 elbow L/R, 2/3 knee L/R, 4/5 hip L/R
        const int NJ = 6;
        static readonly Transform[] _p = new Transform[NJ], _c = new Transform[NJ], _e = new Transform[NJ];
        static readonly Vector3[] _rest = new Vector3[NJ];
        static readonly bool[] _ok = new bool[NJ];
        static readonly float[] _theta = new float[NJ];
        static readonly bool[] _logged = new bool[Slots];
        static readonly string[] SlotNames = { "biceps L", "biceps R", "thigh back L", "thigh back R", "thigh top L", "thigh top R", "calf L", "calf R", "belly fold" };
        static readonly string[] JointNames = { "elbow L", "elbow R", "knee L", "knee R", "hip L", "hip R" };
        static float _nextStatus;
        static int _statSig = -1;
        static readonly StringBuilder _sb = new StringBuilder();

        // Her joints from the character's bones (call when her bones change; cheap, no per-frame lookups after it).
        public static void Bind(Character her)
        {
            for (int j = 0; j < NJ; j++) { _p[j] = _c[j] = _e[j] = null; _ok[j] = false; }
            Joints = 0;
            Any = false;
            if (her == null || her.Root == null) return;
            var missing = new List<string>();
            for (int j = 0; j < NJ; j++)
            {
                string s = (j & 1) == 0 ? "_L" : "_R";
                string pn = j < 2 ? "Shoulder" + s : j < 4 ? "Hip" + s : "Root_M";
                string cn = j < 2 ? "Elbow" + s : j < 4 ? "Knee" + s : "Hip" + s;
                string en = j < 2 ? "Wrist" + s : j < 4 ? "Ankle" + s : "Knee" + s;
                Transform p, c, e;
                if (!her.Bones.TryGetValue(pn, out p) || !her.Bones.TryGetValue(cn, out c) || !her.Bones.TryGetValue(en, out e) || p == null || c == null || e == null)
                {
                    missing.Add(JointNames[j] + " (" + pn + "/" + cn + "/" + en + ")");
                    continue;
                }
                Vector3 rest;
                string why = RestDir(her, p, c, e, out rest);
                if (why != null) { missing.Add(JointNames[j] + " (" + why + ")"); continue; }
                _p[j] = p; _c[j] = c; _e[j] = e; _rest[j] = rest; _ok[j] = true;
                Joints++;
            }
            for (int i = 0; i < Slots; i++) { Amt[i] = 0f; _logged[i] = false; }
            _statSig = -1;
            Status = "";
            Plugin.Log.LogInfo("Bend: " + Joints + " of " + NJ + " joints bound" + (missing.Count > 0 ? "; missing " + string.Join(", ", missing.ToArray()) : ""));
        }

        // The bind-pose direction of E - C in P's local frame, from a mesh of hers that has all three bones (its bind poses place them in
        // one space: bindposes[i] maps mesh space to bone i's local space).
        static string RestDir(Character her, Transform p, Transform c, Transform e, out Vector3 dir)
        {
            dir = Vector3.forward;
            for (int k = 0; k < her.Renderers.Count; k++)
            {
                var r = her.Renderers[k];
                if (r == null || r.sharedMesh == null) continue;
                var bones = r.bones;
                int ip = -1, ic = -1, ie = -1;
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == p && ip < 0) ip = i;
                    else if (bones[i] == c && ic < 0) ic = i;
                    else if (bones[i] == e && ie < 0) ie = i;
                }
                if (ip < 0 || ic < 0 || ie < 0) continue;
                Matrix4x4[] bind = r.sharedMesh.bindposes;
                if (ip >= bind.Length || ic >= bind.Length || ie >= bind.Length) continue;
                Vector3 cm = bind[ic].inverse.MultiplyPoint3x4(Vector3.zero), em = bind[ie].inverse.MultiplyPoint3x4(Vector3.zero);
                Vector3 d = bind[ip].MultiplyVector(em - cm);
                if (d.sqrMagnitude < 1e-14f) continue;
                dir = d.normalized;
                return null;
            }
            return "no mesh has all three bones";
        }

        static float Sm(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        // Once per frame before the GPU chain (GpuContact.Run).
        public static void Update(ContactSettings s)
        {
            Any = false;
            if (Joints == 0 || s == null || s.BendAmount <= 0.0001f)
            {
                if (Amt[0] != 0f || Amt[8] != 0f || Status.Length > 0) { for (int i = 0; i < Slots; i++) Amt[i] = 0f; Status = ""; _statSig = -1; }
                return;
            }
            for (int j = 0; j < NJ; j++)
            {
                _theta[j] = 0f;
                if (!_ok[j]) continue;
                Transform p = _p[j], c = _c[j], e = _e[j];
                if (p == null || c == null || e == null)
                {
                    _ok[j] = false; Joints--;   // destroyed (scene change): the next Bind brings it back
                    Plugin.Log.LogWarning("Bend: " + JointNames[j] + " lost a bone; off until the next bind");
                    continue;
                }
                _theta[j] = Vector3.Angle(p.InverseTransformDirection(e.position - c.position), _rest[j]);
            }
            float k = s.BendAmount;
            float mx = 0f;
            for (int i = 0; i < Slots; i++) Amt[i] = 0f;
            for (int side = 0; side < 2; side++)
            {
                if (_ok[side]) Amt[side] = s.BicepsMm * 0.001f * Sm(20f, 95f, _theta[side]) * k;
                if (_ok[2 + side])
                {
                    float kn = Sm(20f, 110f, _theta[2 + side]) * k;
                    Amt[2 + side] = s.KneeMm * 0.001f * kn;
                    Amt[6 + side] = s.CalfMm * 0.001f * kn;
                }
                if (_ok[4 + side]) Amt[4 + side] = s.HipMm * 0.001f * Sm(40f, 110f, _theta[4 + side]) * k;
            }
            float hipMax = Mathf.Max(_ok[4] ? _theta[4] : 0f, _ok[5] ? _theta[5] : 0f);
            Amt[8] = s.BellyFoldMm * 0.001f * Sm(50f, 110f, hipMax) * k;
            int sig = 0;
            for (int i = 0; i < Slots; i++)
            {
                if (Amt[i] < 0.0005f) Amt[i] = 0f;   // under half a millimetre: nothing to see, and no chain to run for it every frame
                if (Amt[i] > mx) mx = Amt[i];
                sig = sig * 31 + Mathf.RoundToInt(Amt[i] * 2000f);   // 0.5 mm steps
                if (Amt[i] > 0.001f && !_logged[i])
                {
                    _logged[i] = true;
                    Plugin.Log.LogInfo("Bend: first bulge " + SlotNames[i] + " " + (Amt[i] * 1000f).ToString("0.0") + " mm (joint angles: elbow "
                                       + _theta[0].ToString("0") + "/" + _theta[1].ToString("0") + ", knee " + _theta[2].ToString("0") + "/" + _theta[3].ToString("0")
                                       + ", hip " + _theta[4].ToString("0") + "/" + _theta[5].ToString("0") + " deg)");
                }
            }
            Any = mx > 0f;
            // the panel line: rebuilt when the amplitudes change by >= 0.5 mm, at most twice a second
            float now = Time.unscaledTime;
            if (sig != _statSig && now >= _nextStatus)
            {
                _statSig = sig;
                _nextStatus = now + 0.5f;
                if (!Any) Status = "Bend: " + Joints + " joints, at rest";
                else
                {
                    _sb.Length = 0;
                    _sb.Append("Bend: ");
                    bool first = true;
                    for (int i = 0; i < Slots; i++)
                    {
                        if (Amt[i] <= 0.0001f) continue;
                        if (!first) _sb.Append(", ");
                        _sb.Append(SlotNames[i]).Append(' ').Append((Amt[i] * 1000f).ToString("0")).Append(" mm");
                        first = false;
                    }
                    Status = _sb.ToString();
                }
            }
        }

        public static void Release()
        {
            Any = false;
            for (int i = 0; i < Amt.Length; i++) Amt[i] = 0f;
            for (int j = 0; j < NJ; j++) { _p[j] = _c[j] = _e[j] = null; _ok[j] = false; }
            Joints = 0;
            _statSig = -1;
            Status = "";
        }
    }
}
