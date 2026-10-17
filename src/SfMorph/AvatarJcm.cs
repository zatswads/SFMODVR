using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // 0.11.48 the avatar's own joint correctives (docs/SOURCES.md, 'Shoulders and joint correctives').
    //
    // DAZ figures fix their shoulders, elbows, hips, knees, neck and fingers at large angles with corrective blend shapes (Genesis 9
    // body_cbs_*, Genesis 3 / 8 pJCM*), each driven by a formula on a bone's rotation: value = f(l_upperarm rotation z), a TCB spline over
    // angle keys, or a product of helper morphs for the combined poses, times the master dial body_basejointcorrectives. Avatar mode lost
    // them (0.11.29-0.11.47: the DAZ export baked no morphs, the rebuild onto her skeleton dropped blend shapes): the crotch, armpits,
    // neck and wrists tore or collapsed in deep poses. Now daz_export.dsa exports them as blend shapes, tools/sources/daz_correctives.py
    // writes their formulas and the figure's skeleton (rotation order, joint orientation, joint centres) to correctives.txt, Avatar.cs
    // carries the shapes onto the rebuilt mesh, and this file evaluates DAZ's own formulas every frame from her pose:
    //   1. the avatar's bones are not driven (the mesh is skinned to her bones); each avatar bone's pose is reconstructed from the her
    //      bones it was mapped to: W = D x Rb, D = her change since bind (shares blended), Rb = the avatar's rest -> aligned rest, so W is
    //      the bone's world rotation change from DAZ's zero pose;
    //   2. DAZ's local rotation: E = O^T (C^T Wp^-1 W C) O, O = the joint orientation, C = DAZ's axes in her root's space (fitted once
    //      over all joint centres: Unity's FBX import flips handedness, the facing turn and the scale), Euler angles in the bone's own
    //      rotation order (first axis applied first: DAZ's twist axis; checked against DAZ Studio's own world rotations, exact), clamped
    //      to DAZ's joint limits as DAZ does before its formulas read them;
    //   3. the formulas (RPN: channel / morph pushes, keys, + - * /, tcb / linear / constant splines), clamped to each morph's range.
    // The weights are written in a PlayerLoop step just before Unity skins the meshes (after the Animator, FinalIK, DynamicBone and our
    // apply step), so they match the pose drawn this frame. Cost: ~50 bones (two quaternion products and a 3x3 decomposition each) and
    // ~120 small formulas per frame; weights written only when they change by > 0.05.
    internal sealed class JcmTable
    {
        internal sealed class BoneDef { public string Id, Parent, Order; public Vector3 Center, Orient, Min = -Vector3.one * 1e4f, Max = Vector3.one * 1e4f; }

        // K: 0 number, 1 spline key (A, B), 2 rotation channel (S bone, I axis), 3 morph value (S key, I index), 4 binary op (S), 5 spline (I 0 tcb 1 linear 2 const)
        internal struct Tok { public byte K; public float A, B; public string S; public int I; }

        internal sealed class MorphDef
        {
            public string Key, Shape; public float Default, Min, Max; public bool Clamped;
            public readonly List<Tok[]> Sum = new List<Tok[]>(), Mult = new List<Tok[]>();
        }

        public string Figure = "";
        public readonly Dictionary<string, BoneDef> Bones = new Dictionary<string, BoneDef>();
        public readonly List<MorphDef> Morphs = new List<MorphDef>();
        public readonly Dictionary<string, int> MorphIndex = new Dictionary<string, int>();

        static float F(string s) { return float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }
        static Vector3 V(string s) { var p = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); return new Vector3(F(p[0]), F(p[1]), F(p[2])); }

        public static JcmTable Read(string path, out string err)
        {
            err = null;
            var t = new JcmTable();
            var formulas = new List<KeyValuePair<string, KeyValuePair<string, string>>>();
            int ln = 0;
            try
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    ln++;
                    if (raw.Length == 0 || raw[0] == '#') continue;
                    var f = raw.Split('\t');
                    if (f[0] == "figure" && f.Length >= 2) t.Figure = f[1];
                    else if (f[0] == "bone" && f.Length >= 6)
                    {
                        var b = new BoneDef { Id = f[1], Parent = f[2], Order = f[3], Center = V(f[4]), Orient = V(f[5]) };
                        if (f.Length >= 7)
                        {
                            var l = f[6].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (l.Length == 6) { b.Min = new Vector3(F(l[0]), F(l[2]), F(l[4])); b.Max = new Vector3(F(l[1]), F(l[3]), F(l[5])); }
                        }
                        t.Bones[f[1]] = b;
                    }
                    else if (f[0] == "morph" && f.Length >= 7)
                    {
                        var m = new MorphDef { Key = f[1], Default = F(f[2]), Min = F(f[3]), Max = F(f[4]), Clamped = f[5] != "0", Shape = f[6] == "-" ? null : f[6] };
                        t.MorphIndex[m.Key] = t.Morphs.Count;
                        t.Morphs.Add(m);
                    }
                    else if (f[0] == "f" && f.Length >= 4) formulas.Add(new KeyValuePair<string, KeyValuePair<string, string>>(f[1], new KeyValuePair<string, string>(f[2], f[3])));
                }
                foreach (var fo in formulas)
                {
                    if (!t.MorphIndex.TryGetValue(fo.Key, out var mi)) continue;
                    var toks = Parse(t, fo.Value.Value);
                    if (toks == null) continue;
                    (fo.Value.Key == "mult" ? t.Morphs[mi].Mult : t.Morphs[mi].Sum).Add(toks);
                }
            }
            catch (Exception e) { err = "line " + ln + ": " + e.Message; return null; }
            return t;
        }

        static Tok[] Parse(JcmTable t, string rpn)
        {
            var l = new List<Tok>();
            foreach (var s in rpn.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (s.StartsWith("n:", StringComparison.Ordinal)) l.Add(new Tok { K = 0, A = F(s.Substring(2)) });
                else if (s.StartsWith("k:", StringComparison.Ordinal)) { var p = s.Substring(2).Split(','); l.Add(new Tok { K = 1, A = F(p[0]), B = F(p[1]) }); }
                else if (s.StartsWith("r:", StringComparison.Ordinal))
                {
                    int c = s.LastIndexOf(':');
                    char ax = s[s.Length - 1];
                    l.Add(new Tok { K = 2, S = s.Substring(2, c - 2), I = ax == 'x' ? 0 : ax == 'y' ? 1 : 2 });
                }
                else if (s.StartsWith("m:", StringComparison.Ordinal))
                {
                    var key = s.Substring(2);
                    l.Add(new Tok { K = 3, S = key, I = t.MorphIndex.TryGetValue(key, out var mi) ? mi : -1 });
                }
                else if (s == "+" || s == "-" || s == "*" || s == "/") l.Add(new Tok { K = 4, S = s });
                else if (s == "tcb") l.Add(new Tok { K = 5, I = 0 });
                else if (s == "lin") l.Add(new Tok { K = 5, I = 1 });
                else if (s == "const") l.Add(new Tok { K = 5, I = 2 });
                else return null;
            }
            return l.ToArray();
        }
    }

    internal sealed class AvatarJcm
    {
        // one bone of the avatar the formulas read (and the parents they are measured against)
        sealed class DBone
        {
            public string Id;
            public int Parent = -1;            // index in _bones, -1 = the figure (no change)
            public Matrix4x4 O;                // the joint orientation, DAZ axes
            public Vector3 Min, Max;           // DAZ's channel limits (its formulas read the clamped angles)
            public int A, B, C; public float Parity;
            public Transform[] Her; public Quaternion[] HerBindInv; public float[] Share;
            public Quaternion Rb;              // the avatar bone's rest -> aligned rest, her root's space
            public Quaternion W;               // this frame: change from DAZ's zero pose, her root's space
            public Vector3 Euler;              // this frame: DAZ's local rotation channels x y z, degrees
            public int Stamp = -1;
        }

        struct Output { public SkinnedMeshRenderer R; public int Shape, Morph; public float Last; }

        readonly JcmTable _t;
        readonly List<DBone> _bones = new List<DBone>();
        readonly Dictionary<string, int> _boneIndex = new Dictionary<string, int>();
        readonly List<Output> _outs = new List<Output>();
        float[] _val; int[] _stamp;
        int _frame;
        Matrix4x4 _C, _CT;                     // DAZ axes -> her root's space (orthogonal; det -1 when the import flipped handedness)
        Transform _root;
        readonly float[] _stack = new float[64];
        readonly float[] _kx = new float[64], _ky = new float[64];
        int _keys, _sp;
        public string Note = "";
        public int Shapes { get { return _outs.Count; } }
        public float Peak; public string PeakName = "";   // the strongest corrective this frame (smoke test, log)
        public int Frames { get { return _frame; } }       // evaluations so far (smoke test)
        float _rms = -1f, _det;

        AvatarJcm(JcmTable t) { _t = t; }

        public static Matrix4x4 Euler(Vector3 deg, string order)
        {
            var m = Matrix4x4.identity;
            for (int k = 0; k < 3; k++)
            {
                int ax = order[k] - 'X';
                float a = (ax == 0 ? deg.x : ax == 1 ? deg.y : deg.z);
                m = Rot(ax, a) * m;                      // the first axis is applied first
            }
            return m;
        }

        static Matrix4x4 Rot(int ax, float deg)
        {
            float r = deg * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
            var m = Matrix4x4.identity;
            int i = (ax + 1) % 3, j = (ax + 2) % 3;
            m[i, i] = c; m[i, j] = -s; m[j, i] = s; m[j, j] = c;
            return m;
        }

        static Matrix4x4 M(Quaternion q) { return Matrix4x4.Rotate(q); }

        // The bone's local rotation E = R_c(t_c) R_b(t_b) R_a(t_a) (a applied first) -> (t_x, t_y, t_z) in degrees.
        static Vector3 Decompose(Matrix4x4 e, int a, int b, int c, float p)
        {
            float sb = Mathf.Clamp(-p * e[c, a], -1f, 1f);
            float tb = Mathf.Asin(sb), ta, tc;
            if (Mathf.Abs(sb) < 0.99999f) { ta = Mathf.Atan2(p * e[c, b], e[c, c]); tc = Mathf.Atan2(p * e[b, a], e[a, a]); }
            else { ta = 0f; tc = Mathf.Atan2(-p * e[a, b], e[b, b]); }   // gimbal lock: all of it on c
            // Near the middle angle's +-90 deg the order is in gimbal lock: the bone points along the outer axis, so the outer and the
            // first (twist) angle only matter as a sum or difference and Euler splits them at random (an arm straight forward read as
            // 'up 90' + 'twist -95' and fired the arm-up corrective). There the outer angle fades to 0 and the twist takes it over, which
            // is how DAZ itself would pose it (front-back only).
            float w = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.1f, 0.4f, Mathf.Abs(Mathf.Cos(tb))));
            if (w < 1f)
            {
                float tc2 = tc * w, moved = tc - tc2;
                float best = float.MaxValue, ta2 = ta;
                foreach (float s in new[] { 1f, -1f })
                {
                    var t = Rot(c, tc2 * Mathf.Rad2Deg) * Rot(b, tb * Mathf.Rad2Deg) * Rot(a, (ta + s * moved) * Mathf.Rad2Deg);
                    float err = 0f;
                    for (int r = 0; r < 3; r++) for (int k = 0; k < 3; k++) err += Mathf.Abs(t[r, k] - e[r, k]);
                    if (err < best) { best = err; ta2 = ta + s * moved; }
                }
                ta = Mathf.Atan2(Mathf.Sin(ta2), Mathf.Cos(ta2)); tc = tc2;
            }
            var d = new Vector3();
            d[a] = ta * Mathf.Rad2Deg; d[b] = tb * Mathf.Rad2Deg; d[c] = tc * Mathf.Rad2Deg;
            return d;
        }

        // Built once by Avatar.Rig.BuildFrom. avIndex: an avatar bone by (lower-case) name -> its index; par: parents; rest: the avatar
        // bones' rest positions in her root's space (before the fit); rb: rest -> aligned rest; tgt: the her bones each avatar bone takes.
        public static AvatarJcm Bind(JcmTable t, Func<string, int> avIndex, int[] par, Vector3[] rest, Quaternion[] rb,
                                     List<KeyValuePair<Transform, float>>[] tgt, Dictionary<Transform, Matrix4x4> herBind, Transform herRoot)
        {
            var j = new AvatarJcm(t) { _root = herRoot };
            // C: DAZ axes in her root's space from the joint centres (orthogonal Procrustes, reflection allowed)
            var pd = new List<Vector3>(); var ph = new List<Vector3>();
            foreach (var b in t.Bones.Values)
            {
                int i = avIndex(b.Id.ToLowerInvariant());
                if (i < 0) continue;
                pd.Add(b.Center); ph.Add(rest[i]);
            }
            if (pd.Count < 8) { j.Note = "only " + pd.Count + " of the table's joints are in the avatar's skeleton"; return j; }
            Vector3 cd = Vector3.zero, ch = Vector3.zero;
            for (int k = 0; k < pd.Count; k++) { cd += pd[k]; ch += ph[k]; }
            cd /= pd.Count; ch /= pd.Count;
            var cov = new double[3, 3];
            for (int k = 0; k < pd.Count; k++)
            {
                var a = ph[k] - ch; var d = pd[k] - cd;
                for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) cov[r, c] += a[r] * (double)d[c];
            }
            var C = Polar(cov);
            j._C = Matrix4x4.identity;
            for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) j._C[r, c] = (float)C[r, c];
            j._CT = j._C.transpose;
            double num = 0, den = 0, res = 0;
            for (int k = 0; k < pd.Count; k++) { var d = j._C.MultiplyVector(pd[k] - cd); num += Vector3.Dot(d, ph[k] - ch); den += d.sqrMagnitude; }
            float s = den > 0 ? (float)(num / den) : 0f;
            for (int k = 0; k < pd.Count; k++) res += (s * j._C.MultiplyVector(pd[k] - cd) - (ph[k] - ch)).sqrMagnitude;
            float rms = Mathf.Sqrt((float)(res / pd.Count));
            float det = j._C.determinant;
            j._rms = rms; j._det = det;

            // the bones the formulas read, and their parents
            var want = new List<string>();
            foreach (var m in t.Morphs)
                foreach (var l in new[] { m.Sum, m.Mult })
                    foreach (var f in l) foreach (var tk in f) if (tk.K == 2 && !want.Contains(tk.S)) want.Add(tk.S);
            int missing = 0;
            foreach (var id in want) if (j.AddBone(id, avIndex, par, rb, tgt, herBind) < 0) missing++;
            j._val = new float[t.Morphs.Count]; j._stamp = new int[t.Morphs.Count];
            for (int i = 0; i < j._stamp.Length; i++) j._stamp[i] = -1;
            j.Note = "DAZ axes fitted over " + pd.Count + " joints (" + (det < 0f ? "mirrored" : "not mirrored") + ", x" + s.ToString("0.0000", CultureInfo.InvariantCulture) +
                     ", residual " + (rms * 1000f).ToString("0", CultureInfo.InvariantCulture) + " mm), " + j._bones.Count + " bones read" + (missing > 0 ? ", " + missing + " driver bones not in the avatar" : "");
            return j;
        }

        int AddBone(string id, Func<string, int> avIndex, int[] par, Quaternion[] rb, List<KeyValuePair<Transform, float>>[] tgt, Dictionary<Transform, Matrix4x4> herBind)
        {
            if (_boneIndex.TryGetValue(id, out var have)) return have;
            if (!_t.Bones.TryGetValue(id, out var def)) return -1;
            int ai = avIndex(id.ToLowerInvariant());
            if (ai < 0 || tgt[ai] == null || tgt[ai].Count == 0) return -1;
            var b = new DBone { Id = id, Rb = rb[ai], O = Euler(def.Orient, "XYZ"), Min = def.Min, Max = def.Max };
            string o = (def.Order ?? "XYZ").ToUpperInvariant();
            if (o.Length != 3) o = "XYZ";
            b.A = o[0] - 'X'; b.B = o[1] - 'X'; b.C = o[2] - 'X';
            b.Parity = (b.B == (b.A + 1) % 3) ? 1f : -1f;
            var hs = new List<Transform>(); var hi = new List<Quaternion>(); var sh = new List<float>();
            foreach (var kv in tgt[ai])
                if (kv.Key != null && herBind.TryGetValue(kv.Key, out var bm)) { hs.Add(kv.Key); hi.Add(Quaternion.Inverse(bm.rotation)); sh.Add(kv.Value); }
            if (hs.Count == 0) return -1;
            b.Her = hs.ToArray(); b.HerBindInv = hi.ToArray(); b.Share = sh.ToArray();
            int idx = _bones.Count;
            _bones.Add(b); _boneIndex[id] = idx;
            b.Parent = def.Parent != null && _t.Bones.ContainsKey(def.Parent) ? AddBone(def.Parent, avIndex, par, rb, tgt, herBind) : -1;
            return idx;
        }

        // The orthogonal polar factor of a 3x3 matrix (keeps a reflection): X <- (X + X^-T) / 2 until it settles.
        static double[,] Polar(double[,] m)
        {
            var x = (double[,])m.Clone();
            double n = 0; foreach (var v in x) n = Math.Max(n, Math.Abs(v));
            if (n <= 0) return new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) x[r, c] /= n;
            for (int it = 0; it < 60; it++)
            {
                double det = x[0, 0] * (x[1, 1] * x[2, 2] - x[1, 2] * x[2, 1]) - x[0, 1] * (x[1, 0] * x[2, 2] - x[1, 2] * x[2, 0]) + x[0, 2] * (x[1, 0] * x[2, 1] - x[1, 1] * x[2, 0]);
                if (Math.Abs(det) < 1e-12) break;
                // inverse transpose = cofactor matrix / det
                var y = new double[3, 3];
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++)
                    {
                        int r1 = (r + 1) % 3, r2 = (r + 2) % 3, c1 = (c + 1) % 3, c2 = (c + 2) % 3;
                        double cof = x[r1, c1] * x[r2, c2] - x[r1, c2] * x[r2, c1];
                        y[r, c] = 0.5 * (x[r, c] + cof / det);
                    }
                double diff = 0; for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) diff = Math.Max(diff, Math.Abs(y[r, c] - x[r, c]));
                x = y;
                if (diff < 1e-12) break;
            }
            return x;
        }

        // A blend shape of a rebuilt avatar mesh driven by morph `key` (false when the table has no such morph).
        public bool AddOutput(SkinnedMeshRenderer r, int shape, string key)
        {
            if (!_t.MorphIndex.TryGetValue(key, out var mi)) return false;
            _outs.Add(new Output { R = r, Shape = shape, Morph = mi, Last = -1f });
            return true;
        }

        // The corrective key a blend shape of the exported FBX stands for: 'Genesis9__body_cbs_x' / 'body_cbs_x' -> 'body_cbs_x'. DAZ also
        // exports a shape pack's own adjustment of a corrective as 'Genesis9__<Pack>_body_cbs_x' (zero for most characters): not ours.
        public string KeyOf(string shapeName)
        {
            if (string.IsNullOrEmpty(shapeName)) return null;
            string n = shapeName;
            int dot = n.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < n.Length && !_t.MorphIndex.ContainsKey(n)) n = n.Substring(dot + 1);   // Unity's '<mesh>.<shape>'
            if (_t.MorphIndex.TryGetValue(n, out var mi0)) return _t.Morphs[mi0].Shape != null ? n : null;
            int us = n.IndexOf("__", StringComparison.Ordinal);
            if (us >= 0) n = n.Substring(us + 2);
            return _t.MorphIndex.TryGetValue(n, out var mi) && _t.Morphs[mi].Shape != null ? n : null;
        }

        // Every frame (Avatar's PlayerLoop step, before skinning): her pose -> DAZ's channels -> the formulas -> blend shape weights.
        public void Step(float amount)
        {
            if (_root == null || _outs.Count == 0) return;
            _frame++;
            var rootInv = Quaternion.Inverse(_root.rotation);
            for (int i = 0; i < _bones.Count; i++) _bones[i].Stamp = -1;
            for (int i = 0; i < _bones.Count; i++) Pose(i, rootInv);
            Peak = 0f;
            for (int o = 0; o < _outs.Count; o++)
            {
                var op = _outs[o];
                if (op.R == null) continue;
                float v = Value(op.Morph) * amount;
                if (v > Peak) { Peak = v; PeakName = _t.Morphs[op.Morph].Key; }
                float w = Mathf.Clamp(v, 0f, 1.5f) * 100f;
                if (Mathf.Abs(w - op.Last) < 0.05f && !(w == 0f && op.Last != 0f)) continue;
                op.R.SetBlendShapeWeight(op.Shape, w);
                op.Last = w;
                _outs[o] = op;
            }
        }

        // Everything back to 0 (avatar correctives off).
        public void Zero()
        {
            for (int o = 0; o < _outs.Count; o++)
            {
                var op = _outs[o];
                if (op.R != null && op.Last != 0f) op.R.SetBlendShapeWeight(op.Shape, 0f);
                op.Last = 0f; _outs[o] = op;
            }
            Peak = 0f;
        }

        void Pose(int i, Quaternion rootInv)
        {
            var b = _bones[i];
            if (b.Stamp == _frame) return;
            // D: her bones' change since bind, blended by their shares
            Quaternion d = Quaternion.identity; float acc = 0f;
            for (int k = 0; k < b.Her.Length; k++)
            {
                if (b.Her[k] == null) continue;
                var dk = rootInv * b.Her[k].rotation * b.HerBindInv[k];
                float s = b.Share[k];
                if (acc <= 0f) { d = dk; acc = s; }
                else { acc += s; d = Quaternion.Slerp(d, dk, s / acc); }
            }
            b.W = d * b.Rb;
            Matrix4x4 rel;
            if (b.Parent >= 0) { Pose(b.Parent, rootInv); rel = M(Quaternion.Inverse(_bones[b.Parent].W) * b.W); }
            else rel = M(b.W);
            var e = b.O.transpose * (_CT * rel * _C) * b.O;
            var eu = Decompose(e, b.A, b.B, b.C, b.Parity);
            b.Euler = new Vector3(Mathf.Clamp(eu.x, b.Min.x, b.Max.x), Mathf.Clamp(eu.y, b.Min.y, b.Max.y), Mathf.Clamp(eu.z, b.Min.z, b.Max.z));
            b.Stamp = _frame;
        }

        float Value(int mi)
        {
            if (mi < 0) return 0f;
            if (_stamp[mi] == _frame) return _val[mi];
            _stamp[mi] = _frame;
            _val[mi] = 0f;                                         // a cycle reads 0
            var m = _t.Morphs[mi];
            float v = m.Default;
            foreach (var f in m.Sum) v += Eval(f);
            foreach (var f in m.Mult) v *= Eval(f);
            if (m.Clamped) v = Mathf.Clamp(v, m.Min, m.Max);
            if (float.IsNaN(v) || float.IsInfinity(v)) v = 0f;
            _val[mi] = v;
            return v;
        }

        // RPN over a shared stack: each (possibly nested, through a morph push) evaluation works above the slots of its caller
        float Eval(JcmTable.Tok[] f)
        {
            int sp0 = _sp, k0 = _keys;
            for (int i = 0; i < f.Length; i++)
            {
                var t = f[i];
                switch (t.K)
                {
                    case 0: Push(t.A); break;
                    case 1: if (_keys < _kx.Length) { _kx[_keys] = t.A; _ky[_keys] = t.B; _keys++; } break;
                    case 2: { float a = 0f; if (_boneIndex.TryGetValue(t.S, out var bi)) a = _bones[bi].Euler[t.I]; Push(a); break; }
                    case 3: Push(Value(t.I)); break;
                    case 4:
                        if (_sp - sp0 < 2) { _sp = sp0; _keys = k0; return 0f; }
                        { float y = _stack[--_sp], x = _stack[_sp - 1];
                          _stack[_sp - 1] = t.S == "+" ? x + y : t.S == "-" ? x - y : t.S == "*" ? x * y : (Mathf.Abs(y) > 1e-9f ? x / y : 0f); }
                        break;
                    case 5:
                        {
                            if (_sp - sp0 < 2) { _sp = sp0; _keys = k0; return 0f; }
                            int n = Mathf.Min((int)_stack[--_sp], _keys - k0);
                            float x = _stack[_sp - 1];
                            _stack[_sp - 1] = Spline(x, k0, n, t.I);
                            _keys = k0;
                            break;
                        }
                }
            }
            float r = _sp > sp0 ? _stack[_sp - 1] : 0f;
            _sp = sp0; _keys = k0;
            return r;
        }

        void Push(float v) { if (_sp < _stack.Length) _stack[_sp++] = v; }

        // DAZ's spline over (angle, value) keys: TCB with zero tension / continuity / bias (Catmull-Rom tangents from the neighbours),
        // linear or constant; flat beyond the end keys.
        float Spline(float x, int k0, int n, int kind)
        {
            if (n <= 0) return 0f;
            // keys ascending in x (DAZ lists them either way)
            for (int a = k0 + 1; a < k0 + n; a++)
                for (int b = a; b > k0 && _kx[b] < _kx[b - 1]; b--)
                { float tx = _kx[b]; _kx[b] = _kx[b - 1]; _kx[b - 1] = tx; float ty = _ky[b]; _ky[b] = _ky[b - 1]; _ky[b - 1] = ty; }
            if (x <= _kx[k0]) return _ky[k0];
            if (x >= _kx[k0 + n - 1]) return _ky[k0 + n - 1];
            int i = k0;
            while (i < k0 + n - 2 && x > _kx[i + 1]) i++;
            float x0 = _kx[i], x1 = _kx[i + 1], y0 = _ky[i], y1 = _ky[i + 1];
            float h = x1 - x0;
            if (h <= 1e-6f) return y1;
            float u = (x - x0) / h;
            if (kind == 2) return y0;
            if (kind == 1) return y0 + (y1 - y0) * u;
            float m0 = Slope(i, k0, n) * h, m1 = Slope(i + 1, k0, n) * h;
            float u2 = u * u, u3 = u2 * u;
            return (2f * u3 - 3f * u2 + 1f) * y0 + (u3 - 2f * u2 + u) * m0 + (-2f * u3 + 3f * u2) * y1 + (u3 - u2) * m1;
        }

        float Slope(int i, int k0, int n)
        {
            if (n < 2) return 0f;
            int last = k0 + n - 1;
            if (i == k0) return (_ky[i + 1] - _ky[i]) / Mathf.Max(1e-6f, _kx[i + 1] - _kx[i]);
            if (i == last) return (_ky[i] - _ky[i - 1]) / Mathf.Max(1e-6f, _kx[i] - _kx[i - 1]);
            return (_ky[i + 1] - _ky[i - 1]) / Mathf.Max(1e-6f, _kx[i + 1] - _kx[i - 1]);
        }

        // 0.11.48 smoke test (read-only): the evaluator's maths. Euler round trip (compose, then decompose) for three angle sets in every
        // bone's rotation order, away from the gimbal band; the fitted DAZ -> her-root axes orthogonal (|det| 1) with a joint residual
        // under 30 mm; every driven shape's renderer alive.
        public bool SelfTest(out string why)
        {
            var sets = new[] { new Vector3(10f, -20f, 30f), new Vector3(-35f, 15f, -40f), new Vector3(25f, 40f, 5f) };
            float worst = 0f; string at = "";
            foreach (var b in _bones)
                foreach (var a in sets)
                {
                    string order = "" + (char)('X' + b.A) + (char)('X' + b.B) + (char)('X' + b.C);
                    var e = Euler(a, order);
                    var d = Decompose(e, b.A, b.B, b.C, b.Parity);
                    float err = Mathf.Max(Mathf.Abs(Mathf.DeltaAngle(d.x, a.x)), Mathf.Max(Mathf.Abs(Mathf.DeltaAngle(d.y, a.y)), Mathf.Abs(Mathf.DeltaAngle(d.z, a.z))));
                    if (err > worst) { worst = err; at = b.Id + " " + order; }
                }
            int dead = 0;
            foreach (var o in _outs) if (o.R == null) dead++;
            string nums = "Euler round trip " + worst.ToString("0.000", CultureInfo.InvariantCulture) + " deg worst (" + _bones.Count + " bones), axes |det| " +
                          Mathf.Abs(_det).ToString("0.000", CultureInfo.InvariantCulture) + ", joint residual " + (_rms * 1000f).ToString("0", CultureInfo.InvariantCulture) + " mm";
            if (_bones.Count == 0) { why = "no driver bone of the table is in the avatar's skeleton (" + Note + ")"; return false; }
            if (worst > 0.05f) { why = nums + ": the decomposition is wrong at " + at; return false; }
            if (Mathf.Abs(Mathf.Abs(_det) - 1f) > 0.01f) { why = nums + ": the DAZ axes fit is not a rotation"; return false; }
            if (_rms > 0.03f || _rms < 0f) { why = nums + ": the avatar's joints do not match the figure's table (another figure's correctives.txt?)"; return false; }
            if (dead > 0) { why = nums + ", " + dead + " shapes on renderers that went away"; return false; }
            why = nums;
            return true;
        }

        // For the log / smoke test: a bone's DAZ channels this frame, and the current value of a corrective.
        public bool Channels(string bone, out Vector3 deg)
        {
            deg = Vector3.zero;
            if (!_boneIndex.TryGetValue(bone, out var i)) return false;
            deg = _bones[i].Euler;
            return true;
        }

        public float ValueOf(string key) { return _t.MorphIndex.TryGetValue(key, out var mi) && _stamp[mi] == _frame ? _val[mi] : 0f; }

        public string Summary(float min)
        {
            var sb = new StringBuilder();
            for (int o = 0; o < _outs.Count; o++)
            {
                var op = _outs[o];
                if (op.Last < min * 100f) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(_t.Morphs[op.Morph].Key).Append(' ').Append((op.Last / 100f).ToString("0.00", CultureInfo.InvariantCulture));
            }
            return sb.Length > 0 ? sb.ToString() : "none above " + min.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
