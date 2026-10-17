using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // 0.11.47 muscle activation from muscle length (the Chaos Flesh muscle activation constraint: ratio = |origin - insertion| / rest length,
    // act = clamp((1 - ratio) / (1 - threshold), 0, 1)): each muscle of the layout gets one or more strands, polylines from an origin over
    // optional via points to an insertion, each point riding one of her bones. A shortened strand is a contracting muscle; that sees
    // multi-joint muscles (rectus femoris shortens with the hip flexed and the knee straight, not when she sits), the scapula (the
    // deltoids, traps and serratus ride Scapula_L / _R, so whatever turns that bone - the game or an automatic clavicle correction - is in
    // the lengths) and every combination of joints without a hand-written table per pose. What a length can't see is effort: a held squat
    // or arms propping her up barely shorten anything, so Tension keeps the gravity / load terms on top ([Muscles] Driver).
    //
    // Placement: origin / via / insertion points from anatomy, as in the MyoSuite / OpenSim musculoskeletal models (the
    // amathislab/musclemimic_models MJCF sites, Apache-2.0; their legs from Rajagopal et al. (MIT), the arm from MoBL-ARMS (BSD-3 with a
    // non-commercial clause; this free mod only uses it as an anatomy guide)), simplified to 1-3 strands per muscle region of our layout,
    // plus trapezius, serratus and sternocleidomastoid (not in those models) from anatomy. The table gives each point in her default bind
    // pose (mesh space, metres, her left side; "out" = away from her midline) with the bone it rides; Bind maps it onto the bound
    // character's own bind landmarks (each bone's segment to its child: along it by the segment's length, across it by her overall size)
    // and stores it in that bone's bind frame (Character.Bind, mesh -> bone), so it follows her proportions and every bone scale.
    // Rest length L0 = the length with her arms hanging 8 deg from her sides (her bind pose is an A-pose, 41 deg: at bind the middle
    // deltoid is already 9 % short), the rest of her as bound. Thresholds are per strand and were set offline (paths.py in that session's
    // scratchpad: FK on her bind skeleton, ratios for ~35 test poses): start = the ratio where activation begins, full = where it is 1.
    internal static partial class MusclePose
    {
        // <muscle> <S per side (name_L / _R) | C one midline strand | B both sides into one muscle> <strand> <start> <full> <weight>
        // <stretch weight> <stretch start> <stretch full> <scale ref> | <bone> <out> <y> <z> ; ...   (bone "X_s" = that side's bone)
        // The stretch term is for a muscle that shows when lengthened: the abs braced and outlined when she arches or hangs (refs: sharpest
        // with the arms overhead), the lats flaring overhead (0.35, as the old table had it).
        static readonly string[] PathTable =
        {
            "abs C rectus 0.99 0.88 1 0.3 1.03 1.09 trunk | Root_M 0 0.860 0.045 ; Spine1_M 0 1.000 0.080 ; Chest_M 0 1.170 0.085",
            "obliques S external 0.985 0.89 1 0 0 0 trunk | Chest_M 0.120 1.140 -0.010 ; Root_M 0.050 0.900 0.070",
            "obliques S internal 0.985 0.89 1 0 0 0 trunk | Root_M 0.130 0.970 -0.020 ; Chest_M 0.070 1.150 0.075",
            "serratus S upper 0.99 0.92 1 0 0 0 clav | Chest_M 0.110 1.200 0.035 ; Scapula_s 0.075 1.220 -0.100",
            "serratus S lower 0.99 0.92 1 0 0 0 clav | Chest_M 0.115 1.150 0.020 ; Scapula_s 0.090 1.170 -0.090",
            "back B erector 0.99 0.91 1 0 0 0 trunk | Root_M 0.030 0.920 -0.085 ; Spine1_M 0.030 1.000 -0.045 ; Chest_M 0.030 1.150 -0.065",
            "lats S thoracic 0.99 0.85 1 0.35 1.18 1.27 arm | Spine1_M 0.020 1.020 -0.050 ; Chest_M 0.100 1.170 -0.085 ; Shoulder_s 0.178 1.255 -0.030",
            "lats S iliac 0.99 0.85 1 0.35 1.18 1.27 arm | Root_M 0.100 0.960 -0.060 ; Chest_M 0.125 1.150 -0.050 ; Shoulder_s 0.178 1.255 -0.030",
            "lats S teres 0.99 0.85 0.8 0 0 0 arm | Scapula_s 0.090 1.170 -0.090 ; Shoulder_s 0.178 1.255 -0.030",
            "glutes S maximus 0.97 0.86 1 0 0 0 thigh | Root_M 0.030 0.880 -0.110 ; Hip_s 0.140 0.780 -0.050",
            "glutes S medius 0.90 0.65 0.6 0 0 0 thigh | Root_M 0.130 0.970 -0.030 ; Hip_s 0.175 0.860 -0.025",
            "quads S rectus 0.94 0.86 1 0 0 0 thigh | Root_M 0.100 0.910 0.035 ; Hip_s 0.097 0.480 0.045 ; Knee_s 0.095 0.430 0.035",
            "hams S lateral 0.98 0.88 1 0 0 0 thigh | Root_M 0.060 0.800 -0.070 ; Hip_s 0.100 0.500 -0.050 ; Knee_s 0.130 0.430 -0.030",
            "hams S medial 0.98 0.88 1 0 0 0 thigh | Root_M 0.060 0.800 -0.070 ; Hip_s 0.090 0.500 -0.050 ; Knee_s 0.070 0.420 -0.010",
            "adductors S magnus 0.97 0.88 1 0 0 0 thigh | Root_M 0.035 0.820 0.000 ; Hip_s 0.080 0.520 -0.035",
            "calves S soleus 0.99 0.91 1 0 0 0 shin | Knee_s 0.100 0.400 -0.050 ; Ankle_s 0.088 0.050 -0.090",
            "deltoids S front 0.95 0.70 1 0 0 0 arm | Scapula_s 0.140 1.325 -0.020 ; Shoulder_s 0.231 1.238 -0.043",
            "deltoids S middle 0.93 0.68 1 0 0 0 arm | Scapula_s 0.165 1.320 -0.050 ; Shoulder_s 0.231 1.238 -0.043",
            "deltoids S rear 0.97 0.78 1 0 0 0 arm | Scapula_s 0.120 1.300 -0.100 ; Shoulder_s 0.231 1.238 -0.043",
            "biceps S long 0.97 0.82 1 0 0 0 arm | Scapula_s 0.150 1.300 -0.040 ; Shoulder_s 0.190 1.255 -0.020 ; Elbow_s 0.306 1.121 -0.007",
            "triceps S long 0.985 0.95 1 0 0 0 arm | Scapula_s 0.145 1.255 -0.065 ; Shoulder_s 0.267 1.166 -0.057 ; Elbow_s 0.288 1.152 -0.058",
            "forearms S brachioradialis 0.99 0.88 0.6 0 0 0 fore | Shoulder_s 0.273 1.184 -0.038 ; Elbow_s 0.415 0.996 0.049",
            "pecs S clavicular 0.92 0.70 1 0 0 0 arm | Chest_M 0.040 1.340 0.020 ; Shoulder_s 0.184 1.262 -0.020",
            "pecs S sternal 0.97 0.85 1 0 0 0 arm | Chest_M 0.010 1.250 0.070 ; Shoulder_s 0.184 1.262 -0.020",
            "pecs S abdominal 0.97 0.85 1 0 0 0 arm | Chest_M 0.050 1.170 0.080 ; Shoulder_s 0.184 1.262 -0.020",
            "traps B upper 0.97 0.88 1 0 0 0 neck | Head_M 0.025 1.500 -0.085 ; Scapula_s 0.140 1.325 -0.030",
            "traps B cervical 0.97 0.88 1 0 0 0 neck | Neck_M 0.020 1.400 -0.085 ; Scapula_s 0.160 1.320 -0.050",
            "traps B middle 0.98 0.90 1 0 0 0 clav | Chest_M 0.010 1.300 -0.095 ; Scapula_s 0.120 1.300 -0.100",
            "traps B lower 0.98 0.90 1 0 0 0 clav | Chest_M 0.010 1.150 -0.075 ; Scapula_s 0.080 1.280 -0.105",
            "neck S sternocleidomastoid 0.985 0.92 1 0 0 0 neck | Chest_M 0.025 1.345 0.010 ; Head_M 0.050 1.490 -0.035",
        };

        // her default bind skeleton (mesh space, her left side; the table's points are placed against it) and each bone's segment to the
        // child that sets its length (Head_M: none, the head only moves)
        static readonly string[] RefBone = { "Root_M", "Spine1_M", "Chest_M", "Neck_M", "Head_M", "Scapula_L", "Shoulder_L", "Elbow_L", "Wrist_L", "Hip_L", "Knee_L", "Ankle_L", "Toes_L" };
        static readonly string[] RefChild = { "Spine1_M", "Chest_M", "Neck_M", "Head_M", null, "Shoulder_L", "Elbow_L", "Wrist_L", null, "Knee_L", "Ankle_L", "Toes_L", null };
        static readonly Vector3[] RefPos =
        {
            new Vector3(0f, 0.851343f, -0.015725f), new Vector3(0f, 0.988917f, 0.004836f), new Vector3(0f, 1.127761f, 0.013328f),
            new Vector3(0f, 1.379217f, -0.043982f), new Vector3(0f, 1.473521f, -0.043982f), new Vector3(-0.043968f, 1.313478f, -0.066597f),
            new Vector3(-0.158173f, 1.291982f, -0.049614f), new Vector3(-0.286487f, 1.144075f, -0.034738f), new Vector3(-0.42279f, 0.988591f, 0.054493f),
            new Vector3(-0.097966f, 0.874556f, -0.023739f), new Vector3(-0.097966f, 0.469178f, -0.016663f), new Vector3(-0.089244f, 0.083457f, -0.042488f),
            new Vector3(-0.114283f, 0.010744f, 0.074833f),
        };
        const float RefSize = 0.5287f + 0.7913f;     // |Neck_M - Root_M| + |Hip_L - Ankle_L| of that skeleton
        const float NeutralArmDeg = 8f;

        class PathRow
        {
            public string Muscle, Strand, Ref;
            public char Kind;
            public float Start, Full, Weight, SW, S0, S1;
            public string[] Bone;
            public Vector3[] P;
        }
        static List<PathRow> _rows;

        // bound paths (flat arrays; nothing is allocated per frame)
        static Transform[] _pb = new Transform[0];             // the bones the points ride and the scale references read
        static Matrix4x4[] _pbM = new Matrix4x4[0];
        static int[] _ptB = new int[0];                        // point -> index into _pb
        static Vector3[] _ptL = new Vector3[0];                // point in its bone's frame (bind)
        static int[] _stFirst = new int[0], _stCount = new int[0], _stDef = new int[0], _stRef = new int[0];
        static float[] _stL0 = new float[0], _stStart = new float[0], _stFull = new float[0], _stW = new float[0], _stSW = new float[0], _stS0 = new float[0], _stS1 = new float[0];
        static string[] _stName = new string[0];
        public static float[] StrandRatio = new float[0], StrandAct = new float[0];
        static int[] _refA = new int[0], _refB = new int[0], _refC = new int[0];   // scale references: |A - B| (+ |B - C|), rigid under rotation
        static float[] _refBind = new float[0], _refScale = new float[0];
        static string[] _defName = new string[0];             // "abs", "obliques_L", ...
        static float[] _defAct = new float[0];
        static int _defAbs = -1, _defBack = -1;
        static MuscleLayer _mapLayer;
        static int _mapGen = -1, _pathGen;
        static readonly int[] _mapDef = new int[MuscleFlex.Max];
        public static int Strands { get { return _stL0.Length; } }
        public static string PathNote = "";
        // 0.11.47 smoke test (Smoke.cs): strands the table asks for on this rig (C rows once, S / B rows per side), those skipped at the
        // last bind (a bone or a scale reference missing) and why
        public static int ExpectedStrands, SkippedStrands;
        public static string SkippedNote = "";
        public static int MuscleCount { get { return _defName.Length; } }
        public static int PathBones { get { return _pb.Length; } }
        static float _nextLenLog;
        static string _lastLenLog = "";

        static List<PathRow> Rows()
        {
            if (_rows != null) return _rows;
            _rows = new List<PathRow>();
            foreach (var line in PathTable)
            {
                var halves = line.Split('|');
                var h = halves[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var pts = halves[1].Split(';');
                var r = new PathRow { Muscle = h[0], Kind = h[1][0], Strand = h[2], Ref = h[9], Bone = new string[pts.Length], P = new Vector3[pts.Length] };
                r.Start = F(h[3]); r.Full = F(h[4]); r.Weight = F(h[5]); r.SW = F(h[6]); r.S0 = F(h[7]); r.S1 = F(h[8]);
                for (int i = 0; i < pts.Length; i++)
                {
                    var t = pts[i].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    r.Bone[i] = t[0];
                    r.P[i] = new Vector3(F(t[1]), F(t[2]), F(t[3]));
                }
                _rows.Add(r);
            }
            return _rows;
        }
        static float F(string s) { return float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }

        static int RefIndex(string bone)
        {
            string b = bone.EndsWith("_R", StringComparison.Ordinal) ? bone.Substring(0, bone.Length - 2) + "_L" : bone;
            for (int i = 0; i < RefBone.Length; i++) if (RefBone[i] == b) return i;
            return -1;
        }
        // a landmark's reference position, mirrored for her right side
        static Vector3 RefOf(int i, bool right) { var p = RefPos[i]; if (right) p.x = -p.x; return p; }
        static string SideName(string n, bool right) { return right && n.EndsWith("_L", StringComparison.Ordinal) ? n.Substring(0, n.Length - 2) + "_R" : n; }

        // her bind landmark (mesh space): the bone's bind position, or the reference one when the bone isn't skinned
        static Vector3 Landmark(Character her, string bone, bool right, out bool real)
        {
            Transform t;
            Matrix4x4 b;
            real = Get(her, bone, out t) && her.Bind.TryGetValue(t, out b);
            if (real) return MeshPos(her, t);
            int i = RefIndex(bone);
            return i >= 0 ? RefOf(i, right) : Vector3.zero;
        }

        // the table's point (reference skeleton) on her: from its bone along the bone's segment by the segment's length, across by her size
        static Vector3 MapPoint(Character her, string bone, Vector3 refP, bool right, float size)
        {
            int i = RefIndex(bone);
            if (i < 0) return refP;
            bool real;
            Vector3 a0 = RefOf(i, right), a1 = Landmark(her, bone, right, out real);
            Vector3 d = refP - a0;
            if (RefChild[i] == null) return a1 + d * size;
            int c = RefIndex(RefChild[i]);
            Vector3 s0 = RefOf(c, right) - a0, s1 = Landmark(her, SideName(RefChild[i], right), right, out real) - a1;
            if (s0.sqrMagnitude < 1e-8f || s1.sqrMagnitude < 1e-8f) return a1 + d * size;
            Vector3 u0 = s0.normalized;
            float along = Vector3.Dot(d, u0);
            Vector3 across = d - along * u0;
            Quaternion q = Quaternion.FromToRotation(u0, s1.normalized);
            return a1 + q * (u0 * along * (s1.magnitude / s0.magnitude) + across * size);
        }

        // the bone a point rides: the named one if it is skinned (has a bind pose), else the nearest skinned one up the chain
        static Transform Attach(Character her, string bone)
        {
            for (int guard = 0; guard < 4 && bone != null; guard++)
            {
                Transform t;
                if (Get(her, bone, out t) && her.Bind.ContainsKey(t)) return t;
                if (bone.StartsWith("Scapula_", StringComparison.Ordinal) || bone == "Neck_M") bone = "Chest_M";
                else if (bone == "Head_M") bone = "Neck_M";
                else if (bone == "Spine1_M") bone = "Root_M";
                else if (bone.StartsWith("Toes_", StringComparison.Ordinal)) bone = "Ankle_" + bone.Substring(5);
                else bone = null;
            }
            return null;
        }

        static void BindPaths(Character her)
        {
            _pathGen++;
            _mapLayer = null;
            var bones = new List<Transform>();
            var ptB = new List<int>(); var ptL = new List<Vector3>();
            var sFirst = new List<int>(); var sCount = new List<int>(); var sDef = new List<int>(); var sRef = new List<int>();
            var sL0 = new List<float>(); var sStart = new List<float>(); var sFull = new List<float>(); var sW = new List<float>();
            var sSW = new List<float>(); var sS0 = new List<float>(); var sS1 = new List<float>(); var sName = new List<string>();
            var rA = new List<int>(); var rB = new List<int>(); var rC = new List<int>(); var rBind = new List<float>(); var rKey = new List<string>();
            var defs = new List<string>();
            var miss = new List<string>();
            Func<Transform, int> boneIdx = t => { int i = bones.IndexOf(t); if (i < 0) { bones.Add(t); i = bones.Count - 1; } return i; };
            bool real;
            Vector3 root = Landmark(her, "Root_M", false, out real), neck = Landmark(her, "Neck_M", false, out real);
            Vector3 hipL = Landmark(her, "Hip_L", false, out real), ankL = Landmark(her, "Ankle_L", false, out real);
            float size = Mathf.Clamp(((neck - root).magnitude + (hipL - ankL).magnitude) / RefSize, 0.5f, 2f);
            // her arms hanging (the rest length's pose): each arm turned about its shoulder from the bind direction to 8 deg from her side
            var armRot = new Quaternion[2];
            var armPivot = new Vector3[2];
            float bindElev = 0f;
            for (int s = 0; s < 2; s++)
            {
                bool right = s == 1, r1, r2;
                armPivot[s] = Landmark(her, right ? "Shoulder_R" : "Shoulder_L", right, out r1);
                Vector3 el = Landmark(her, right ? "Elbow_R" : "Elbow_L", right, out r2);
                float e = NeutralArmDeg * Mathf.Deg2Rad;
                var down = new Vector3((right ? 1f : -1f) * Mathf.Sin(e), -Mathf.Cos(e), 0f);   // her left = -x
                armRot[s] = (el - armPivot[s]).sqrMagnitude > 1e-8f ? Quaternion.FromToRotation(el - armPivot[s], down) : Quaternion.identity;
                if (s == 0) bindElev = Vector3.Angle(el - armPivot[s], Vector3.down);
            }
            foreach (var row in Rows())
            {
                int nside = row.Kind == 'C' ? 1 : 2;
                for (int s = 0; s < nside; s++)
                {
                    bool right = s == 1;
                    string def = row.Kind == 'S' ? row.Muscle + (right ? "_R" : "_L") : row.Muscle;
                    string label = def + "." + row.Strand + (row.Kind == 'B' ? (right ? " R" : " L") : "");
                    int n = row.P.Length;
                    var idx = new int[n];
                    var loc = new Vector3[n];
                    var neu = new Vector3[n];
                    bool ok = true;
                    for (int i = 0; i < n && ok; i++)
                    {
                        string bone = row.Bone[i].EndsWith("_s", StringComparison.Ordinal) ? row.Bone[i].Substring(0, row.Bone[i].Length - 1) + (right ? "R" : "L") : row.Bone[i];
                        Vector3 rp = row.P[i];
                        rp.x = right ? rp.x : -rp.x;                               // "out" -> mesh x: her left = -x
                        Vector3 mp = MapPoint(her, bone, rp, right, size);
                        Transform t = Attach(her, bone);
                        if (t == null) { ok = false; miss.Add(label + " (" + bone + ")"); break; }
                        idx[i] = boneIdx(t);
                        loc[i] = her.Bind[t].MultiplyPoint3x4(mp);
                        int arm = bone.StartsWith("Shoulder_", StringComparison.Ordinal) || bone.StartsWith("Elbow_", StringComparison.Ordinal) || bone.StartsWith("Wrist_", StringComparison.Ordinal) ? (right ? 1 : 0) : -1;
                        neu[i] = arm >= 0 ? armPivot[arm] + armRot[arm] * (mp - armPivot[arm]) : mp;
                    }
                    if (!ok) continue;
                    float l0 = 0f;
                    for (int i = 1; i < n; i++) l0 += (neu[i] - neu[i - 1]).magnitude;
                    if (l0 < 0.01f) { miss.Add(label + " (no length)"); continue; }
                    // the strand's scale reference: rigid bone-to-bone distances it spans (her size, a bone length slider)
                    string key = row.Ref + (row.Ref == "trunk" || row.Ref == "neck" ? "" : right ? "_R" : "_L");
                    int ri = rKey.IndexOf(key);
                    if (ri < 0)
                    {
                        string sd = right ? "_R" : "_L";
                        string a, b, c = null;
                        switch (row.Ref)
                        {
                            case "trunk": a = "Root_M"; b = "Spine1_M"; c = "Chest_M"; break;
                            case "neck": a = "Chest_M"; b = "Neck_M"; break;
                            case "clav": a = "Scapula" + sd; b = "Shoulder" + sd; break;
                            case "arm": a = "Shoulder" + sd; b = "Elbow" + sd; break;
                            case "fore": a = "Elbow" + sd; b = "Wrist" + sd; break;
                            case "thigh": a = "Hip" + sd; b = "Knee" + sd; break;
                            default: a = "Knee" + sd; b = "Ankle" + sd; break;
                        }
                        Transform ta, tb, tc = null;
                        if (Get(her, a, out ta) && Get(her, b, out tb) && her.Bind.ContainsKey(ta) && her.Bind.ContainsKey(tb) && (c == null || (Get(her, c, out tc) && her.Bind.ContainsKey(tc))))
                        {
                            float lb = (MeshPos(her, tb) - MeshPos(her, ta)).magnitude + (tc != null ? (MeshPos(her, tc) - MeshPos(her, tb)).magnitude : 0f);
                            if (lb > 0.01f)
                            {
                                rKey.Add(key); rA.Add(boneIdx(ta)); rB.Add(boneIdx(tb)); rC.Add(tc != null ? boneIdx(tc) : -1); rBind.Add(lb);
                                ri = rKey.Count - 1;
                            }
                        }
                        if (ri < 0) { miss.Add(label + " (scale ref " + key + ")"); continue; }
                    }
                    int di = defs.IndexOf(def);
                    if (di < 0) { defs.Add(def); di = defs.Count - 1; }
                    sFirst.Add(ptB.Count); sCount.Add(n); sDef.Add(di); sRef.Add(ri); sL0.Add(l0);
                    sStart.Add(row.Start); sFull.Add(Mathf.Min(row.Full, row.Start - 0.005f)); sW.Add(row.Weight);
                    sSW.Add(row.SW); sS0.Add(row.S0); sS1.Add(Mathf.Max(row.S1, row.S0 + 0.005f)); sName.Add(label);
                    for (int i = 0; i < n; i++) { ptB.Add(idx[i]); ptL.Add(loc[i]); }
                }
            }
            _pb = bones.ToArray(); _pbM = new Matrix4x4[_pb.Length];
            _ptB = ptB.ToArray(); _ptL = ptL.ToArray();
            _stFirst = sFirst.ToArray(); _stCount = sCount.ToArray(); _stDef = sDef.ToArray(); _stRef = sRef.ToArray(); _stL0 = sL0.ToArray();
            _stStart = sStart.ToArray(); _stFull = sFull.ToArray(); _stW = sW.ToArray(); _stSW = sSW.ToArray(); _stS0 = sS0.ToArray(); _stS1 = sS1.ToArray();
            _stName = sName.ToArray();
            StrandRatio = new float[_stL0.Length]; StrandAct = new float[_stL0.Length];
            _refA = rA.ToArray(); _refB = rB.ToArray(); _refC = rC.ToArray(); _refBind = rBind.ToArray(); _refScale = new float[_refBind.Length];
            _defName = defs.ToArray(); _defAct = new float[_defName.Length];
            _defAbs = defs.IndexOf("abs"); _defBack = defs.IndexOf("back");
            PathNote = _stL0.Length + " strands of " + _defName.Length + " muscles";
            int expect = 0;
            foreach (var row in Rows()) expect += row.Kind == 'C' ? 1 : 2;
            ExpectedStrands = expect; SkippedStrands = miss.Count; SkippedNote = string.Join(", ", miss.ToArray());
            Plugin.Log.LogInfo("Muscles: lengths: " + PathNote + " on " + _pb.Length + " bones, her bind arm " + bindElev.ToString("0") + " deg from her side (rest lengths with the arms " +
                               NeutralArmDeg.ToString("0") + " deg), size x" + size.ToString("0.00") + (miss.Count > 0 ? "; skipped " + string.Join(", ", miss.ToArray()) : ""));
        }

        // 0.11.47 smoke test: each strand's activation with her at her bind pose (each point where its bone's bind pose puts it, every
        // scale reference 1): the A-pose shortens the middle deltoid ~9 %, nothing should be near full. The largest, and which strand.
        public static float BindAct(Character her, out string worst)
        {
            worst = "";
            if (her == null || _stL0.Length == 0) return 0f;
            float best = 0f;
            for (int k = 0; k < _stL0.Length; k++)
            {
                int f = _stFirst[k];
                float len = 0f;
                Vector3 prev = Vector3.zero;
                bool ok = true;
                for (int j = 0; j < _stCount[k] && ok; j++)
                {
                    Transform t = _pb[_ptB[f + j]];
                    Matrix4x4 b;
                    if (t == null || !her.Bind.TryGetValue(t, out b)) { ok = false; break; }
                    Vector3 p = b.inverse.MultiplyPoint3x4(_ptL[f + j]);
                    if (j > 0) len += (p - prev).magnitude;
                    prev = p;
                }
                if (!ok) continue;
                float ratio = len / _stL0[k];
                float a = Mathf.Clamp01((_stStart[k] - ratio) / (_stStart[k] - _stFull[k])) * _stW[k];
                if (_stSW[k] > 0f) a = Mathf.Max(a, _stSW[k] * Mathf.Clamp01((ratio - _stS0[k]) / (_stS1[k] - _stS0[k])));
                if (a > best) { best = a; worst = _stName[k] + " " + ratio.ToString("0.00", CultureInfo.InvariantCulture); }
            }
            return best;
        }

        // the live strand with the highest activation (Smoke.cs)
        public static float LiveMax(out string name)
        {
            name = "";
            float best = 0f;
            for (int k = 0; k < StrandAct.Length && k < _stName.Length; k++) if (StrandAct[k] > best) { best = StrandAct[k]; name = _stName[k]; }
            return best;
        }

        static void ReleasePaths()
        {
            _pb = new Transform[0]; _pbM = new Matrix4x4[0];
            _stL0 = new float[0]; _stFirst = _stCount = _stDef = _stRef = new int[0];
            _defName = new string[0]; _defAct = new float[0]; _defAbs = _defBack = -1;
            StrandRatio = StrandAct = new float[0];
            _mapLayer = null;
            PathNote = "";
            ExpectedStrands = SkippedStrands = 0; SkippedNote = "";
        }

        // this frame's strand lengths -> _defAct (per muscle: the strongest strand); false when nothing is bound or a bone is gone
        static bool UpdatePaths()
        {
            if (_stL0.Length == 0) return false;
            for (int i = 0; i < _pb.Length; i++)
            {
                if (_pb[i] == null) { ReleasePaths(); return false; }
                _pbM[i] = _pb[i].localToWorldMatrix;
            }
            for (int r = 0; r < _refBind.Length; r++)
            {
                Vector3 a = Pos(_refA[r]), b = Pos(_refB[r]);
                float l = (b - a).magnitude;
                if (_refC[r] >= 0) l += (Pos(_refC[r]) - b).magnitude;
                _refScale[r] = l > 1e-5f ? l / _refBind[r] : 1f;
            }
            for (int d = 0; d < _defAct.Length; d++) _defAct[d] = 0f;
            for (int k = 0; k < _stL0.Length; k++)
            {
                int f = _stFirst[k];
                Vector3 prev = _pbM[_ptB[f]].MultiplyPoint3x4(_ptL[f]);
                float len = 0f;
                for (int j = 1; j < _stCount[k]; j++)
                {
                    Vector3 p = _pbM[_ptB[f + j]].MultiplyPoint3x4(_ptL[f + j]);
                    len += (p - prev).magnitude;
                    prev = p;
                }
                float ratio = len / (_stL0[k] * _refScale[_stRef[k]]);
                float a = Mathf.Clamp01((_stStart[k] - ratio) / (_stStart[k] - _stFull[k])) * _stW[k];
                if (_stSW[k] > 0f) a = Mathf.Max(a, _stSW[k] * Mathf.Clamp01((ratio - _stS0[k]) / (_stS1[k] - _stS0[k])));
                StrandRatio[k] = ratio;
                StrandAct[k] = a;
                if (a > _defAct[_stDef[k]]) _defAct[_stDef[k]] = a;
            }
            return true;
        }
        static Vector3 Pos(int i) { var m = _pbM[i]; return new Vector3(m.m03, m.m13, m.m23); }

        // the length activation of the layer's muscle k (-1: no strand for it)
        static float LenAct(MuscleLayer L, int k)
        {
            if (L != _mapLayer || _mapGen != _pathGen)
            {
                _mapLayer = L;
                _mapGen = _pathGen;
                for (int i = 0; i < _mapDef.Length; i++) _mapDef[i] = i < L.Count ? Array.IndexOf(_defName, L.MuscleName[i]) : -1;
            }
            int d = k < _mapDef.Length ? _mapDef[k] : -1;
            return d >= 0 ? _defAct[d] : -1f;
        }
        static float DefAct(int d) { return d >= 0 && d < _defAct.Length ? _defAct[d] : 0f; }

        // every 10 s, when it changed: the strands furthest from their rest length (ratio and activation), for tuning start / full
        static void LogLengths()
        {
            float now = Time.unscaledTime;
            if (now < _nextLenLog || _stL0.Length == 0) return;
            _nextLenLog = now + 10f;
            var sb = new StringBuilder();
            var used = new bool[_stL0.Length];
            for (int n = 0; n < 8; n++)
            {
                int best = -1;
                float bd = 0.02f;
                for (int k = 0; k < _stL0.Length; k++)
                {
                    if (used[k]) continue;
                    float dv = Mathf.Abs(1f - StrandRatio[k]) + StrandAct[k];
                    if (dv > bd) { bd = dv; best = k; }
                }
                if (best < 0) break;
                used[best] = true;
                sb.Append(sb.Length > 0 ? ", " : "").Append(_stName[best]).Append(' ').Append(StrandRatio[best].ToString("0.00", CultureInfo.InvariantCulture));
                if (StrandAct[best] >= 0.05f) sb.Append(" (").Append(StrandAct[best].ToString("0.00", CultureInfo.InvariantCulture)).Append(')');
            }
            string line = sb.Length > 0 ? sb.ToString() : "all at rest length";
            if (line == _lastLenLog) return;
            _lastLenLog = line;
            Plugin.Log.LogInfo("Muscles: length ratios (driver " + Driver + "; L / rest, activation): " + line + "; scapula " + ScapRot[0].ToString("0") + "/" + ScapRot[1].ToString("0") +
                               " deg, arm elev chest " + Elev[0].ToString("0") + "/" + Elev[1].ToString("0") + " scapula " + GhElev[0].ToString("0") + "/" + GhElev[1].ToString("0"));
        }
    }
}
