using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace SfMorph
{
    // A generic RBF pose driver, the same idea as UE's Pose Driver node (interpolative solver, Gaussian kernel, swing / twist distance):
    // one driver bone, its rotation measured in an eval space (a parent bone's local frame), N pose targets in that space, M outputs per
    // target. Per frame: phi_j = exp(-(d(x, t_j) / (Radius * Scale_j))^2), weights w = C^T phi with C = inverse of the targets' own kernel
    // matrix (precomputed once at build, so each target gives weight 1 at itself and 0 at the others), clamped to [0, 1], dropped below
    // Threshold, normalised only when they sum above one; outputs = sum_i w_i * Out[i, o].
    //
    // Why interpolative: with a handful of targets it is smooth and exact at the targets (the additive solver needs many targets and always
    // normalises). Why a mandatory rest target with zero output: without it the weights of the other targets fill up to one and some
    // corrective is always fully on. Far from every target all phi go to 0, so the outputs fade to 0 (a safe default for poses nobody
    // authored). If the kernel matrix is near singular (targets closer than ~10 deg, or the radius too small) the driver falls back to
    // the additive solve (w_i = phi_i) and says so in the log.
    //
    // Cost: N kernel evaluations (one Vector3.Angle each) + an N x N multiply-add; N <= 12, no allocations per frame.
    internal sealed class RbfPose
    {
        public readonly string Name;
        public readonly int N, M;                      // targets, outputs
        public readonly string[] TargetNames;
        public readonly Quaternion[] Targets;          // rotations of the driver bone in the eval space
        public readonly float[] Scale;                 // per-target radius scale (1 = Radius)
        public readonly float[,] Out;                  // [target, output]
        public Vector3 Axis = Vector3.right;           // twist axis in the driver bone's local space (along the bone); swing = where it points
        public float Radius = 50f;                     // degrees
        public float TwistWeight = 0f;                 // 0 = swing only; > 0 adds the twist difference (deg) x this to the distance
        public float Threshold = 0.001f;
        public readonly float[] Weights, Outputs;      // this frame's, read by anyone
        public float Nearest;                          // distance (deg) to the nearest target this frame
        public bool Interpolative { get; private set; }
        public float MinPivot { get; private set; }

        readonly float[] _phi;
        readonly float[,] _c;

        public RbfPose(string name, int n, int m)
        {
            Name = name; N = n; M = m;
            TargetNames = new string[n]; Targets = new Quaternion[n]; Scale = new float[n]; Out = new float[n, m];
            Weights = new float[n]; Outputs = new float[m]; _phi = new float[n]; _c = new float[n, n];
            for (int i = 0; i < n; i++) { Scale[i] = 1f; Targets[i] = Quaternion.identity; }
        }

        // Swing: the angle between where the bone points in a and in b. Twist (optional): the roll about the bone of b^-1 a.
        public float Dist(Quaternion a, Quaternion b)
        {
            float d = Vector3.Angle(a * Axis, b * Axis);
            if (TwistWeight > 0f) d += TwistWeight * TwistDeg(a, b, Axis);
            return d;
        }

        public static float TwistDeg(Quaternion a, Quaternion b, Vector3 axis)
        {
            Quaternion r = Quaternion.Inverse(b) * a;
            float p = r.x * axis.x + r.y * axis.y + r.z * axis.z;
            float ang = 2f * Mathf.Atan2(p, r.w) * Mathf.Rad2Deg;
            while (ang > 180f) ang -= 360f;
            while (ang < -180f) ang += 360f;
            return Mathf.Abs(ang);
        }

        float Kernel(float d, int j)
        {
            float r = d / Mathf.Max(1e-3f, Radius * Scale[j]);
            return Mathf.Exp(-r * r);
        }

        // Once, after the targets are set: C = (A^T)^-1 with A[k, j] = phi_j(t_k), by Gauss-Jordan with partial pivoting in double.
        // false (and the additive fallback) when a pivot is below 1e-6.
        public bool Solve()
        {
            int n = N;
            var a = new double[n, 2 * n];
            for (int k = 0; k < n; k++)
                for (int j = 0; j < n; j++)
                    a[j, k] = Kernel(Dist(Targets[k], Targets[j]), j);   // transposed: row j, column k
            for (int i = 0; i < n; i++) a[i, n + i] = 1.0;
            double minPiv = double.MaxValue;
            for (int col = 0; col < n; col++)
            {
                int piv = col; double best = Math.Abs(a[col, col]);
                for (int r = col + 1; r < n; r++) { double v = Math.Abs(a[r, col]); if (v > best) { best = v; piv = r; } }
                if (best < minPiv) minPiv = best;
                if (best < 1e-6) { MinPivot = (float)best; Interpolative = false; return false; }
                if (piv != col) for (int k = 0; k < 2 * n; k++) { double t = a[col, k]; a[col, k] = a[piv, k]; a[piv, k] = t; }
                double inv = 1.0 / a[col, col];
                for (int k = 0; k < 2 * n; k++) a[col, k] *= inv;
                for (int r = 0; r < n; r++)
                {
                    if (r == col) continue;
                    double f = a[r, col];
                    if (f == 0.0) continue;
                    for (int k = 0; k < 2 * n; k++) a[r, k] -= f * a[col, k];
                }
            }
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) _c[i, j] = (float)a[i, n + j];
            MinPivot = (float)minPiv;
            Interpolative = true;
            return true;
        }

        public void Evaluate(Quaternion x)
        {
            int n = N;
            float near = float.MaxValue;
            for (int j = 0; j < n; j++)
            {
                float d = Dist(x, Targets[j]);
                if (d < near) near = d;
                _phi[j] = Kernel(d, j);
            }
            Nearest = near;
            float sum = 0f;
            for (int i = 0; i < n; i++)
            {
                float w;
                if (Interpolative) { w = 0f; for (int j = 0; j < n; j++) w += _c[i, j] * _phi[j]; }
                else w = _phi[i];
                if (float.IsNaN(w) || w < Threshold) w = 0f; else if (w > 1f) w = 1f;
                Weights[i] = w; sum += w;
            }
            if (sum > 1f) { float s = 1f / sum; for (int i = 0; i < n; i++) Weights[i] *= s; }
            for (int o = 0; o < M; o++)
            {
                float v = 0f;
                for (int i = 0; i < n; i++) v += Weights[i] * Out[i, o];
                Outputs[o] = v;
            }
        }

        public int IndexOf(string target) { for (int i = 0; i < N; i++) if (TargetNames[i] == target) return i; return -1; }
    }

    // 0.11.47 pose correctives: automatic clavicle / scapula for her (user: "clavs are a big issue with base game animations").
    //
    // The game's animations barely move her Scapula_L / _R (AdvancedSkeleton: Chest_M > Scapula_X > Shoulder_X > Elbow_X), so a raised arm
    // turns in a shoulder that stays down and the skin of the shoulder top and armpit collapses. Real shoulders follow the scapulohumeral
    // rhythm: beyond about 30 deg of arm elevation roughly a third of the extra elevation is the shoulder girdle (clavicle elevation, scapula
    // upward rotation / posterior tilt), the shoulder protracts when the arm reaches forward or across and retracts when it goes back.
    //
    // Per side one RbfPose: driver = Shoulder_X's rotation in Chest_M's frame (swing only; the twist axis is the upper arm toward Elbow_X),
    // targets (side-mirrored by building them from her own lateral axis, never by copying quaternions): rest (her bind A-pose, zero output),
    // arm down, abduction 45 / 90 / 150, scaption 90, flexion 90 / 160, extension back 45, across the chest. Outputs = the Scapula's offset
    // in degrees: x elevation (the shoulder end up), y protraction (forward), z posterior tilt (the shoulder blade's top back, the roll
    // that comes with upward rotation). Elevation and tilt are 0.25 / 0.12 x the target's elevation beyond max(30, rest elevation), so the
    // bind pose and below get nothing; protraction is a per-target table.
    //
    // Writing (the Overlap model, see Overlap.Unwind): Body.ApplyAll calls Step for her after the bone sliders, after Overlap.Unwind and
    // BEFORE HerBody / Overlap read her pose, so colliders, the yield and the secondary motion all see the corrected shoulders. Scapula and
    // Shoulder each have a tracker: a value that differs from our last write is the animation's of this frame (the new base); one that
    // still holds our write means the animation did not write the bone, and the base stays (nothing accumulates either way). Scapula gets
    // base x the offset (applied in Chest_M's frame about its own pivot); Shoulder gets the inverse, so the upper arm keeps exactly the
    // animation's orientation in the chest frame and the arm still points where the animation puts it (the shoulder joint itself moves a
    // few cm with the girdle, which is the point; a hand resting on something follows by that much). Switching off fades the offset out
    // over 0.3 s and then gives both bones back.
    //
    // Published for other systems (muscles, wrinkles, folds; read-only, valid when Frame == Time.frameCount):
    //   PoseDriver.Drivers[side]           the RbfPose (Weights[i], TargetNames[i], Outputs[0..2] before Amount)
    //   PoseDriver.ShoulderElevation[side] deg, the upper arm from hanging straight down (0) to straight up (180), animation pose
    //   PoseDriver.ShoulderPlane[side]     deg, where the arm points around her: 0 out to the side, +90 forward, -90 back, 135 across
    //   PoseDriver.ScapulaOffsetDeg[side]  (elevation, protraction, tilt) as written this frame (after Amount and fade; 0 when off)
    //   side: PoseDriver.Left = 0, Right = 1.
    internal static class PoseDriver
    {
        public const int Left = 0, Right = 1;
        public static readonly RbfPose[] Drivers = new RbfPose[2];
        public static readonly float[] ShoulderElevation = new float[2];
        public static readonly float[] ShoulderPlane = new float[2];
        public static readonly Vector3[] ScapulaOffsetDeg = new Vector3[2];
        public static int Frame = -1;          // Time.frameCount of the last evaluation
        public static string Status = "";

        public static ConfigEntry<bool> On, Debug;
        public static ConfigEntry<float> ClavicleAmount;

        public static void Bind(ConfigFile cfg)
        {
            // Off by default for the first release: built, not yet seen in the headset (frame and signs come from her bind pose and are
            // self-checked in the log, but a wrong sign would drop her shoulders in every animation). Flip after the headset check.
            On = cfg.Bind("Correctives", "On", false, "Pose correctives: her clavicles and shoulder blades follow her arms (shoulders rise when the arms go up, come forward when they reach forward or across, go back when they reach back), on top of the game's animations. The arm itself still points where the animation puts it.");
            ClavicleAmount = cfg.Bind("Correctives", "ClavicleAmount", 0.6f, new ConfigDescription("How much of the natural shoulder-girdle motion is added (1 = the full scapulohumeral rhythm; the game's animations may already contain some).", new AcceptableValueRange<float>(0f, 1f)));
            Debug = cfg.Bind("Correctives", "Debug", false, "Every 10 s a 'Pose correctives' log line per shoulder: arm elevation / plane, the driver's target weights and the shoulder offset, to find poses that look wrong.");
        }

        const float FadeTime = 0.3f;
        const float ElevK = 0.25f, TiltK = 0.12f;            // deg of offset per deg of arm elevation beyond the start
        const float MaxElev = 30f, MaxProt = 25f, MaxTilt = 20f;
        const float MinTargetGap = 20f;                      // deg: a target this close to an earlier one is dropped (ill-conditioned solve)
        const float SameEps = 1e-6f;

        sealed class Track
        {
            public Transform T; public Quaternion Base, Last; public bool Has;
            public Quaternion Read()
            {
                var cur = T.localRotation;
                if (!Has || !Same(cur, Last)) Base = cur;
                return Base;
            }
            public void Write(Quaternion q) { T.localRotation = q; Last = T.localRotation; Has = true; }
            public void GiveBack() { if (Has && T != null && Same(T.localRotation, Last)) T.localRotation = Base; Has = false; }
        }

        sealed class Side
        {
            public string Tag;
            public Transform Chest, Scap, Sh;
            public Transform[] K, J;       // chains: Chest_M's child .. Scapula's parent; Scapula's child .. Shoulder's parent (usually empty)
            public Track TScap, TSh;
            public Vector3 U, F, L;        // in Chest_M's local frame: up, forward, lateral (out on this side)
            public Vector3 AxE, AxP, AxT;  // offset axes in Chest_M's frame: elevation, protraction, posterior tilt
            public RbfPose Rbf;
            public float RestElev;
        }

        static readonly Side[] _sides = new Side[2];
        static Character _her;
        static Transform _root;
        static float _fade, _nextLog, _failUntil;
        static bool _built, _logNow;
        static string _buildFail;

        // The panel's "Log pose" button: the next Step logs both shoulders (a pose that looks wrong becomes a target from that line).
        public static void LogNow() { _logNow = true; }

        // Body.ApplyAll, for her, after Overlap.Unwind and before HerBody / Overlap.
        public static void Step(Character her, float dt)
        {
            if (her == null || her.Root == null) { Release(); return; }
            if (Time.unscaledTime < _failUntil) return;
            if (!ReferenceEquals(her, _her) || !ReferenceEquals(her.Root, _root) || (_built && _sides[0] != null && (_sides[0].Sh == null || _sides[0].Scap == null)))
            {
                Release();
                _her = her; _root = her.Root;
                _built = Build(her);
            }
            if (!_built) { Status = "off: " + _buildFail; return; }

            bool on = On.Value;
            float target = on ? 1f : 0f;
            _fade = dt > 0f ? Mathf.MoveTowards(_fade, target, dt / FadeTime) : target;
            float amt = Mathf.Clamp01(ClavicleAmount.Value) * _fade;
            bool write = amt > 0f;

            for (int s = 0; s < 2; s++)
            {
                var sd = _sides[s];
                if (sd == null) continue;
                Quaternion scapB = sd.TScap.Read(), shB = sd.TSh.Read();
                Quaternion k = Quaternion.identity, jq = Quaternion.identity;
                for (int i = 0; i < sd.K.Length; i++) k = k * sd.K[i].localRotation;
                for (int i = 0; i < sd.J.Length; i++) jq = jq * sd.J[i].localRotation;
                Quaternion scapC = k * scapB;                       // the scapula in Chest_M's frame (animation pose)
                Quaternion x = scapC * jq * shB;                    // the upper arm in Chest_M's frame (animation pose)
                sd.Rbf.Evaluate(x);

                Vector3 d = x * sd.Rbf.Axis;
                ShoulderElevation[s] = Vector3.Angle(d, -sd.U);
                ShoulderPlane[s] = Mathf.Atan2(Vector3.Dot(d, sd.F), Vector3.Dot(d, sd.L)) * Mathf.Rad2Deg;

                if (!write)
                {
                    ScapulaOffsetDeg[s] = Vector3.zero;
                    sd.TScap.GiveBack(); sd.TSh.GiveBack();
                    continue;
                }
                float e = Mathf.Clamp(sd.Rbf.Outputs[0] * amt, -MaxElev, MaxElev);
                float p = Mathf.Clamp(sd.Rbf.Outputs[1] * amt, -MaxProt, MaxProt);
                float t = Mathf.Clamp(sd.Rbf.Outputs[2] * amt, -MaxTilt, MaxTilt);
                if (float.IsNaN(e) || float.IsNaN(p) || float.IsNaN(t)) { e = p = t = 0f; }
                ScapulaOffsetDeg[s] = new Vector3(e, p, t);
                Quaternion r = Quaternion.AngleAxis(p, sd.AxP) * Quaternion.AngleAxis(e, sd.AxE) * Quaternion.AngleAxis(t, sd.AxT);
                // scapula: offset in Chest_M's frame about its own pivot; shoulder: whatever keeps the upper arm's chest-frame rotation
                Quaternion scapN = Quaternion.Inverse(k) * r * scapC;
                Quaternion shN = Quaternion.Inverse(jq) * Quaternion.Inverse(r * scapC) * x;
                if (!Ok(scapN) || !Ok(shN)) { sd.TScap.GiveBack(); sd.TSh.GiveBack(); continue; }
                sd.TScap.Write(scapN.normalized);
                sd.TSh.Write(shN.normalized);
            }
            Frame = Time.frameCount;
            Status = on ? (_sides[0] != null && _sides[1] != null ? "on: both shoulders" : "on: one shoulder (see the log)") : _fade > 0f ? "fading out" : "";
            if (_logNow || (Debug.Value && Time.unscaledTime >= _nextLog))
            {
                _nextLog = Time.unscaledTime + 10f;
                LogLine(_logNow ? "asked" : "every 10 s");
                _logNow = false;
            }
        }

        static void LogLine(string why)
        {
            var sb = new StringBuilder("Pose correctives (" + why + ", amount " + (Mathf.Clamp01(ClavicleAmount.Value) * _fade).ToString("0.00") + "):");
            for (int s = 0; s < 2; s++)
            {
                var sd = _sides[s];
                if (sd == null) continue;
                var rb = sd.Rbf;
                sb.Append(" | ").Append(sd.Tag).Append(" elev ").Append(ShoulderElevation[s].ToString("0")).Append(" plane ").Append(ShoulderPlane[s].ToString("0"))
                  .Append(" nearest ").Append(rb.Nearest.ToString("0")).Append(" deg, w");
                for (int i = 0; i < rb.N; i++) if (rb.Weights[i] >= 0.01f) sb.Append(' ').Append(rb.TargetNames[i]).Append(' ').Append(rb.Weights[i].ToString("0.00"));
                var o = ScapulaOffsetDeg[s];
                sb.Append(" -> scapula elev ").Append(o.x.ToString("0.0")).Append(" prot ").Append(o.y.ToString("0.0")).Append(" tilt ").Append(o.z.ToString("0.0"));
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        // Called by Body after an exception: everything back, retry in 10 s.
        public static void Fail()
        {
            try { Release(); } catch { }
            _failUntil = Time.unscaledTime + 10f;
        }

        // Gives the bones back (only where they still hold our write) and forgets her.
        public static void Release()
        {
            for (int s = 0; s < 2; s++)
            {
                var sd = _sides[s];
                if (sd != null) { try { sd.TScap.GiveBack(); sd.TSh.GiveBack(); } catch { } }
                _sides[s] = null; Drivers[s] = null;
                ScapulaOffsetDeg[s] = Vector3.zero; ShoulderElevation[s] = 0f; ShoulderPlane[s] = 0f;
            }
            _her = null; _root = null; _built = false; _fade = 0f; Frame = -1;
            Status = "";
        }

        // ---- build ----------------------------------------------------------------------------------------------------------

        static bool Build(Character her)
        {
            _buildFail = "";
            Transform chest = Bone(her, "Chest_M");
            if (chest == null) { _buildFail = "no Chest_M on her rig"; Plugin.Log.LogInfo("Pose correctives: " + _buildFail); return false; }
            Matrix4x4 cb;
            if (!her.Bind.TryGetValue(chest, out cb)) { _buildFail = "Chest_M has no bind pose"; Plugin.Log.LogInfo("Pose correctives: " + _buildFail); return false; }
            Func<Transform, Vector3?> pos = tr => { Matrix4x4 m; return tr != null && her.Bind.TryGetValue(tr, out m) ? (Vector3?)m.inverse.GetColumn(3) : null; };
            Transform shL = Bone(her, "Shoulder_L"), shR = Bone(her, "Shoulder_R"), pelvis = Bone(her, "Root_M"), neck = Bone(her, "Neck_M");
            var pc = pos(chest); var pl = pos(shL); var pr = pos(shR); var pp = pos(pelvis); var pn = pos(neck);
            if (!pl.HasValue || !pr.HasValue) { _buildFail = "no bind pose for Shoulder_L / _R"; Plugin.Log.LogInfo("Pose correctives: " + _buildFail); return false; }
            // her frame in the meshes' bind space (as Body.Directions), then into Chest_M's local frame
            Vector3 upM = pp.HasValue ? pc.Value - pp.Value : pn.HasValue ? pn.Value - pc.Value : Vector3.up;
            Vector3 leftM = pl.Value - pr.Value;
            Vector3 U = cb.MultiplyVector(upM).normalized, Lf = cb.MultiplyVector(leftM).normalized;
            Lf = (Lf - U * Vector3.Dot(Lf, U)).normalized;
            Vector3 F = Vector3.Cross(U, Lf).normalized;
            string fwdCheck = "no toes to check forward";
            Transform toe = Bone(her, "Toes_L"), ankle = Bone(her, "Ankle_L");
            var pt = pos(toe); var pa = pos(ankle);
            if (pt.HasValue && pa.HasValue)
            {
                if (Vector3.Dot(cb.MultiplyVector(pt.Value - pa.Value), F) < 0f) { F = -F; fwdCheck = "forward flipped (toes were behind the ankles)"; }
                else fwdCheck = "forward checked by the toes";
            }
            string upCheck = pn.HasValue ? (Vector3.Dot(cb.MultiplyVector(pn.Value - pc.Value), U) > 0f ? "up checked by the neck" : "WARNING: the neck is below the chest along up") : "no neck to check up";

            var sb = new StringBuilder("Pose correctives: clavicle drivers (" + upCheck + ", " + fwdCheck + ")");
            int built = 0;
            for (int s = 0; s < 2; s++)
            {
                string x = s == Left ? "_L" : "_R";
                string why;
                var sd = BuildSide(her, chest, cb, pos, x, U, F, s == Left ? Lf : -Lf, out why);
                _sides[s] = sd; Drivers[s] = sd != null ? sd.Rbf : null;
                if (sd == null) { sb.Append("; ").Append(x.Substring(1)).Append(": off: ").Append(why); continue; }
                built++;
                sb.Append("; ").Append(sd.Tag).Append(": ").Append(why);
            }
            Plugin.Log.LogInfo(sb.ToString());
            if (built == 0) { _buildFail = "no shoulder could be set up (see the log)"; return false; }
            return true;
        }

        static Side BuildSide(Character her, Transform chest, Matrix4x4 cb, Func<Transform, Vector3?> pos, string x, Vector3 U, Vector3 F, Vector3 L, out string why)
        {
            why = "";
            Transform sh = Bone(her, "Shoulder" + x), el = Bone(her, "Elbow" + x);
            if (sh == null || el == null) { why = "no Shoulder" + x + " / Elbow" + x; return null; }
            Transform scap = Bone(her, "Scapula" + x);
            if (scap == null) scap = sh.parent;
            if (scap == null || scap == chest || !IsAncestor(scap, sh) || !IsAncestor(chest, scap)) { why = "no bone between Chest_M and Shoulder" + x; return null; }
            Matrix4x4 sb;
            if (!her.Bind.TryGetValue(sh, out sb)) { why = "Shoulder" + x + " has no bind pose"; return null; }
            var psh = pos(sh); var pel = pos(el); var psc = pos(scap);
            if (!psh.HasValue || !pel.HasValue) { why = "no bind pose for the upper arm"; return null; }

            var sd = new Side { Tag = x.Substring(1), Chest = chest, Scap = scap, Sh = sh, U = U, F = F, L = L };
            sd.K = Chain(chest, scap.parent);
            sd.J = Chain(scap, sh.parent);
            sd.TScap = new Track { T = scap };
            sd.TSh = new Track { T = sh };

            // the upper arm at bind: its axis in Shoulder's frame, its rotation and direction in Chest_M's frame
            Vector3 axis = sb.MultiplyVector(pel.Value - psh.Value).normalized;
            Quaternion qRest = (cb * sb.inverse).rotation;
            Vector3 restDir = cb.MultiplyVector(pel.Value - psh.Value).normalized;
            float axisErr = Vector3.Angle(qRest * axis, restDir);
            float liveErr = Vector3.Angle(axis, el.localPosition.sqrMagnitude > 1e-10f ? el.localPosition.normalized : axis);
            sd.RestElev = Vector3.Angle(restDir, -U);

            // offset axes (AngleAxis(a, Cross(x, y)) turns x toward y): elevation lifts the girdle's lateral end toward up, protraction
            // brings it forward, tilt turns the top of the shoulder blade back (up toward -forward)
            Vector3 lat = psc.HasValue ? cb.MultiplyVector(psh.Value - psc.Value) : L;
            lat = lat - U * Vector3.Dot(lat, U);
            if (lat.sqrMagnitude < 1e-8f || Vector3.Dot(lat.normalized, L) < 0.3f) lat = L;
            lat.Normalize();
            sd.AxE = Vector3.Cross(lat, U).normalized;
            sd.AxP = Vector3.Cross(lat, F).normalized;
            sd.AxT = Vector3.Cross(U, -F).normalized;

            // targets: (name, elevation, plane, protraction); plane 0 = out to the side, 90 = forward, -90 = back
            float e0 = Mathf.Max(30f, sd.RestElev);
            var names = new List<string>(); var dirs = new List<Vector3>(); var prots = new List<float>();
            names.Add("rest"); dirs.Add(restDir); prots.Add(0f);
            AddT(names, dirs, prots, "down", Dir(U, F, L, 0f, 0f), 0f);
            AddT(names, dirs, prots, "abd45", Dir(U, F, L, 45f, 0f), 0f);
            AddT(names, dirs, prots, "abd90", Dir(U, F, L, 90f, 0f), 0f);
            AddT(names, dirs, prots, "abd150", Dir(U, F, L, 150f, 0f), 0f);
            AddT(names, dirs, prots, "scap90", Dir(U, F, L, 90f, 45f), 6f);
            AddT(names, dirs, prots, "flex90", Dir(U, F, L, 90f, 90f), 12f);
            AddT(names, dirs, prots, "flex160", Dir(U, F, L, 160f, 90f), 6f);
            AddT(names, dirs, prots, "back45", Dir(U, F, L, 45f, -90f), -10f);
            AddT(names, dirs, prots, "across", Dir(U, F, L, 90f, 135f), 20f);

            int n = names.Count;
            var rb = new RbfPose("Shoulder" + x + " in Chest_M", n, 3) { Axis = axis };
            for (int i = 0; i < n; i++)
            {
                rb.TargetNames[i] = names[i];
                rb.Targets[i] = Quaternion.FromToRotation(restDir, dirs[i]) * qRest;   // only where the bone points matters (swing)
                float el2 = Vector3.Angle(dirs[i], -U);
                bool zero = i == 0 || names[i] == "down";
                float extra = zero ? 0f : Mathf.Max(0f, el2 - e0);
                rb.Out[i, 0] = ElevK * extra;
                rb.Out[i, 1] = prots[i];
                rb.Out[i, 2] = TiltK * extra;
            }
            // radius: the mean nearest-neighbour distance between targets (UE's automatic radius), kept within 30..70 deg
            float sum = 0f;
            for (int i = 0; i < n; i++)
            {
                float best = float.MaxValue;
                for (int j = 0; j < n; j++) if (j != i) best = Mathf.Min(best, Vector3.Angle(dirs[i], dirs[j]));
                sum += best;
            }
            rb.Radius = Mathf.Clamp(n > 1 ? sum / n : 45f, 30f, 70f);
            bool interp = rb.Solve();
            sd.Rbf = rb;

            var w = new StringBuilder();
            w.Append(n).Append(" targets (");
            for (int i = 0; i < n; i++) { if (i > 0) w.Append(' '); w.Append(names[i]); }
            w.Append("), radius ").Append(rb.Radius.ToString("0")).Append(" deg, ").Append(interp ? "interpolative (min pivot " + rb.MinPivot.ToString("0.000") + ")" : "ADDITIVE fallback (singular targets)")
             .Append(", bind elevation ").Append(sd.RestElev.ToString("0")).Append(" deg, axis check ").Append(axisErr.ToString("0.0")).Append(" / ").Append(liveErr.ToString("0.0")).Append(" deg")
             .Append(", chains ").Append(sd.K.Length).Append('/').Append(sd.J.Length).Append(", scapula '").Append(scap.name).Append('\'');
            if (axisErr > 5f) w.Append(" WARNING: the bind rotation and the bind positions disagree");
            why = w.ToString();
            return sd;
        }

        // A target unless it lies within MinTargetGap of one already there (near-duplicate targets make the solve ill-conditioned).
        static void AddT(List<string> names, List<Vector3> dirs, List<float> prots, string name, Vector3 d, float prot)
        {
            for (int i = 0; i < dirs.Count; i++) if (Vector3.Angle(dirs[i], d) < MinTargetGap) return;
            names.Add(name); dirs.Add(d); prots.Add(prot);
        }

        // The upper arm's direction for an elevation (0 hanging, 180 straight up) and a plane (0 out to the side, 90 forward, -90 back).
        static Vector3 Dir(Vector3 U, Vector3 F, Vector3 L, float elevDeg, float planeDeg)
        {
            float e = elevDeg * Mathf.Deg2Rad, p = planeDeg * Mathf.Deg2Rad;
            Vector3 h = L * Mathf.Cos(p) + F * Mathf.Sin(p);
            return (-U * Mathf.Cos(e) + h * Mathf.Sin(e)).normalized;
        }

        // The bones strictly between `top` and `bottom`'s child, top-down: top's child .. bottom (empty when bottom == top).
        static Transform[] Chain(Transform top, Transform bottom)
        {
            var l = new List<Transform>();
            for (var t = bottom; t != null && t != top; t = t.parent) l.Add(t);
            l.Reverse();
            return l.ToArray();
        }

        static bool IsAncestor(Transform a, Transform b)
        {
            for (var t = b.parent; t != null; t = t.parent) if (t == a) return true;
            return false;
        }

        static Transform Bone(Character her, string name)
        {
            Transform t;
            return her.Bones.TryGetValue(name, out t) ? t : null;
        }

        static bool Same(Quaternion a, Quaternion b)
        {
            return Mathf.Abs(a.x - b.x) < SameEps && Mathf.Abs(a.y - b.y) < SameEps && Mathf.Abs(a.z - b.z) < SameEps && Mathf.Abs(a.w - b.w) < SameEps;
        }

        static bool Ok(Quaternion q)
        {
            return !(float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w) || float.IsInfinity(q.x) || float.IsInfinity(q.w));
        }
    }
}
