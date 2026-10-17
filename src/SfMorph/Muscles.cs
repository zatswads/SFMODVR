using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    internal class MuscleSettings
    {
        public bool On;
        public float Fitness = 0.3f;      // resting tone: every muscle's activation at rest (0 none, 1 full definition)
        public float Gain = 1f;           // how much of the pose's tension adds on top (0 = rest tone only)
        public float Strength = 1f;       // depth of the relief (normal map)
        public float Shade = 0.3f;        // groove shade in the albedo copy (0 = none)
        public float Obliques = 0.6f;     // 0.11.20: the obliques' relief / shade x this (their activation as the shader sees it)
        public bool Core = true, Back = true, Legs = true, Arms = true;
        public int Debug;                 // 0 off, 1 activation (tint), 2 muscles in their own colours
        public float Attack = 0.25f, Release = 0.8f;
    }

    // 0.10.0 authored muscle definition (tools/muscles/author.py): one layout per UV layout (only "body", her body atlas), blended by
    // Wrinkles into the same normal-map / albedo copy as the creases. Detail = slope / groove depth / coverage (GPU only); Region = two
    // muscle ids and the first one's share per texel (point-fetched by the blend, which interpolates the resulting activation).
    internal class MuscleLayer
    {
        public string Name;
        public Texture2D Detail, Region;
        public Texture2D Height;           // 0.11.12: the relief's signed height (16 bits over R / G), null in an older bundle
        public float SlopeMax = 0.6f, HeightMax = 0.016f;
        public readonly List<string> MuscleName = new List<string>();
        public readonly List<string> Area = new List<string>();     // core / back / legs / arms
        public readonly List<string> Side = new List<string>();     // L / R / C
        public int Count { get { return MuscleName.Count; } }

        public static List<MuscleLayer> Parse(string text, Func<string, Texture2D> load, List<string> notes)
        {
            var res = new List<MuscleLayer>();
            MuscleLayer cur = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (t[0] == "layout" && t.Length >= 2)
                {
                    cur = new MuscleLayer { Name = t[1] };
                    float sm;
                    if (t.Length >= 5 && float.TryParse(t[4], NumberStyles.Float, CultureInfo.InvariantCulture, out sm) && sm > 0.01f) cur.SlopeMax = sm;
                    if (t.Length >= 6 && float.TryParse(t[5], NumberStyles.Float, CultureInfo.InvariantCulture, out sm) && sm > 0.001f) cur.HeightMax = sm;
                    cur.Detail = load("muscle_" + cur.Name + "_detail");
                    cur.Region = load("muscle_" + cur.Name + "_region");
                    cur.Height = load("muscle_" + cur.Name + "_height");   // optional
                    if (cur.Detail == null || cur.Region == null) { notes.Add(cur.Name + ": textures missing"); cur = null; continue; }
                    res.Add(cur);
                }
                else if (t[0] == "muscle" && cur != null && t.Length >= 5)
                {
                    int idx;
                    if (!int.TryParse(t[1], out idx) || idx != cur.Count + 1 || idx > Wrinkles.MaxRegions) { notes.Add(cur.Name + ": bad line '" + line + "'"); continue; }
                    cur.MuscleName.Add(t[2]);
                    cur.Area.Add(t[3]);
                    cur.Side.Add(t[4]);
                }
            }
            return res;
        }
    }

    // 0.10.0 her muscle tension from her pose, like wrinkle maps are faded in by her expression: joint angles against the bind pose (spine
    // flexion / extension / side bend / twist of Neck_M vs Root_M, hip flexion / abduction, knee, ankle plantar flexion, shoulder elevation
    // and forward / back swing, elbow, wrist) and a gravity load (the trunk held leaning forward or back with the hips bent, the legs under
    // her weight, the arms propping her up) -> a tension 0..1 per muscle of the layout. Wrinkles turns that into the activations it
    // blends: act = Fitness + (1 - Fitness) x clamp01(Gain x tension), each muscle with attack / release. Everything is CPU and per
    // frame cheap (about 30 bone reads); the blend only re-runs when an activation moves by 1/32, at most 10 times a second.
    // Strain (the skin's measured compression) was the other candidate driver: it says where skin folds, not which muscle works, and a
    // contracted muscle under a straight joint (a braced belly, a held squat) barely compresses the skin, so the pose and load drive it.
    internal static class MusclePose
    {
        // joints (index): trunk 0, hip L/R 1/2, knee 3/4, ankle 5/6, shoulder 7/8, elbow 9/10, wrist 11/12
        const int NJ = 13;
        static readonly Transform[] _p = new Transform[NJ], _c = new Transform[NJ], _e = new Transform[NJ];
        static readonly Vector3[] _rest = new Vector3[NJ];        // rest direction of E - C in P's local frame
        static readonly Vector3[] _front = new Vector3[NJ];       // her front (mesh +z) in P's local frame at bind
        static readonly Vector3[] _side = new Vector3[NJ];        // the joint's outer side (her left for the trunk) in P's local frame
        static readonly Vector3[] _ref = new Vector3[NJ];         // a second rest direction in P's frame (ankle: the shin; shoulder: chest down)
        static readonly bool[] _ok = new bool[NJ];
        static readonly string[] JointNames = { "trunk", "hip L", "hip R", "knee L", "knee R", "ankle L", "ankle R", "shoulder L", "shoulder R", "elbow L", "elbow R", "wrist L", "wrist R" };
        static Transform _chest, _root, _neck, _spine, _head;
        static Vector3 _chestFront, _rootFront;                   // her front in Chest_M's / Root_M's local frame at bind
        static Vector3 _headRest, _chestLeft, _headFront;         // 0.11.23: Neck_M -> Head_M and her left in Chest_M's frame, her face's front in Head_M's
        static float _legLen = 0.8f, _armLen = 0.45f;
        static Character _bound;
        static int _boundBones = -1;
        public static int Joints;
        public static string BindNote = "";

        // the last pose (degrees / 0..1), for the log and the panel
        public static float Flex, Side, Twist, BackLoad, AbsLoad;
        // 0.11.23: her head vs her chest (degrees): forward tip (+ chin down), side tilt (+ toward her left), turn (+ toward her right)
        public static float HeadFlex, HeadSide, HeadTurn;
        // 0.11.20: how far she bends toward each side (0..1, [0] her left): the oblique on that side bunches up at full strength whatever
        // [Muscles] Obliques says (that setting tames them when only the abs work)
        public static readonly float[] SideBend = new float[2];

        // 0.11.20 (user: no abs while she is pregnant): the game's pregnancy setting swaps her body for reika_pregnantUpperBody_lvN /
        // pregnantLowerBody_lvN; the worn level (0 none) is read from her drawn renderers once a second. Any level: no abs (relief, shade,
        // bulge); the obliques half at level 1, none from level 2 (a stretched belly shows neither).
        public static int Pregnancy { get; private set; }
        static float _pregAt = -10f;
        static void CheckPregnancy(Character her)
        {
            if (Time.unscaledTime - _pregAt < 1f) return;
            _pregAt = Time.unscaledTime;
            int lv = 0;
            foreach (var r in her.Renderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                string n = r.name;
                if (n.IndexOf("pregnant", StringComparison.OrdinalIgnoreCase) < 0) continue;
                int l = 1, j = n.LastIndexOf("lv", StringComparison.OrdinalIgnoreCase);
                if (j >= 0 && j + 2 < n.Length && char.IsDigit(n[j + 2])) l = n[j + 2] - '0';
                if (l > lv) lv = l;
            }
            if (lv != Pregnancy) Plugin.Log.LogInfo("Muscles: pregnancy level " + lv + (lv > 0 ? ": no abs" + (lv >= 2 ? " or obliques" : ", obliques at half") : ": abs and obliques back"));
            Pregnancy = lv;
        }
        public static float PregnancyScale(string muscle)
        {
            if (Pregnancy <= 0 || muscle == null) return 1f;
            if (muscle == "abs") return 0f;
            if (muscle.StartsWith("obliques", StringComparison.Ordinal)) return Pregnancy >= 2 ? 0f : 0.5f;
            return 1f;
        }
        public static float ObliquesFactor(float setting, string side)
        {
            float k = Mathf.Max(0f, setting);
            if (k >= 1f) return k;
            return k + (1f - k) * SideBend[side == "R" ? 1 : 0];
        }
        public static readonly float[] HipFlex = new float[2], Abd = new float[2], Knee = new float[2], Plantar = new float[2], Elev = new float[2],
            ArmSwing = new float[2], Elbow = new float[2], Wrist = new float[2], LegLoad = new float[2], ArmLoad = new float[2];

        static bool Get(Character her, string n, out Transform t) { return her.Bones.TryGetValue(n, out t) && t != null; }

        static Vector3 MeshPos(Character her, Transform t)
        {
            Matrix4x4 b;
            return her.Bind.TryGetValue(t, out b) ? b.inverse.MultiplyPoint3x4(Vector3.zero) : Vector3.zero;
        }

        static Vector3 ToLocal(Character her, Transform p, Vector3 meshDir)
        {
            Matrix4x4 b;
            if (!her.Bind.TryGetValue(p, out b)) return Vector3.zero;
            var v = b.MultiplyVector(meshDir);
            return v.sqrMagnitude > 1e-12f ? v.normalized : Vector3.zero;
        }

        public static void Bind(Character her)
        {
            _bound = her;
            _boundBones = her != null ? her.Bones.Count : -1;
            Joints = 0;
            for (int j = 0; j < NJ; j++) { _ok[j] = false; _p[j] = _c[j] = _e[j] = null; }
            _chest = _root = _neck = _spine = _head = null;
            if (her == null) { BindNote = ""; return; }
            var miss = new List<string>();
            for (int j = 0; j < NJ; j++)
            {
                string s = j == 0 ? "" : ((j - 1) & 1) == 0 ? "_L" : "_R";
                float sx = s == "_R" ? 1f : -1f;          // her left = -x (mesh space)
                string pn, cn, en;
                switch (j == 0 ? 0 : (j - 1) / 2 + 1)
                {
                    case 0: pn = "Root_M"; cn = "Spine1_M"; en = "Neck_M"; break;
                    case 1: pn = "Root_M"; cn = "Hip" + s; en = "Knee" + s; break;
                    case 2: pn = "Hip" + s; cn = "Knee" + s; en = "Ankle" + s; break;
                    case 3: pn = "Knee" + s; cn = "Ankle" + s; en = "Toes" + s; break;
                    case 4: pn = "Chest_M"; cn = "Shoulder" + s; en = "Elbow" + s; break;
                    case 5: pn = "Shoulder" + s; cn = "Elbow" + s; en = "Wrist" + s; break;
                    default: pn = "Elbow" + s; cn = "Wrist" + s; en = "MiddleFinger1" + s; break;
                }
                Transform p, c, e;
                if (!Get(her, pn, out p) || !Get(her, cn, out c) || !Get(her, en, out e) || !her.Bind.ContainsKey(p) || !her.Bind.ContainsKey(c) || !her.Bind.ContainsKey(e))
                { miss.Add(JointNames[j] + " (" + pn + "/" + cn + "/" + en + ")"); continue; }
                Vector3 d = MeshPos(her, e) - MeshPos(her, c);
                if (d.sqrMagnitude < 1e-10f) { miss.Add(JointNames[j] + " (no bind length)"); continue; }
                _rest[j] = ToLocal(her, p, d);
                _front[j] = ToLocal(her, p, Vector3.forward);
                _side[j] = ToLocal(her, p, new Vector3(j == 0 ? -1f : sx, 0f, 0f));
                if (j == 5 || j == 6) _ref[j] = ToLocal(her, p, MeshPos(her, c) - MeshPos(her, p));   // the shin (knee -> ankle)
                else if (j == 7 || j == 8) _ref[j] = ToLocal(her, p, Vector3.down);                    // chest down
                _p[j] = p; _c[j] = c; _e[j] = e; _ok[j] = _rest[j] != Vector3.zero && _front[j] != Vector3.zero;
                if (_ok[j]) Joints++;
            }
            Get(her, "Chest_M", out _chest); Get(her, "Root_M", out _root); Get(her, "Neck_M", out _neck); Get(her, "Spine1_M", out _spine);
            if (_chest != null) _chestFront = ToLocal(her, _chest, Vector3.forward);
            if (_root != null) _rootFront = ToLocal(her, _root, Vector3.forward);
            if (Get(her, "Head_M", out _head) && _chest != null && _neck != null && her.Bind.ContainsKey(_head) && her.Bind.ContainsKey(_neck))
            {
                _headRest = ToLocal(her, _chest, MeshPos(her, _head) - MeshPos(her, _neck));
                _chestLeft = ToLocal(her, _chest, new Vector3(-1f, 0f, 0f));
                _headFront = ToLocal(her, _head, Vector3.forward);
                if (_headRest == Vector3.zero || _headFront == Vector3.zero) _head = null;
            }
            else _head = null;
            Transform h, a, sh, w;
            if (Get(her, "Hip_L", out h) && Get(her, "Ankle_L", out a)) _legLen = Mathf.Max(0.3f, (MeshPos(her, h) - MeshPos(her, a)).magnitude);
            if (Get(her, "Shoulder_L", out sh) && Get(her, "Wrist_L", out w)) _armLen = Mathf.Max(0.2f, (MeshPos(her, sh) - MeshPos(her, w)).magnitude);
            BindNote = Joints + " of " + NJ + " joints" + (miss.Count > 0 ? ", missing " + string.Join(", ", miss.ToArray()) : "");
            Plugin.Log.LogInfo("Muscles: " + BindNote + " (leg " + _legLen.ToString("0.00") + " m, arm " + _armLen.ToString("0.00") + " m)");
        }

        static float Sm(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        // the current direction of E - C in P's frame
        static bool Dir(int j, out Vector3 r)
        {
            r = Vector3.zero;
            if (!_ok[j]) return false;
            Transform p = _p[j], c = _c[j], e = _e[j];
            if (p == null || c == null || e == null) { _ok[j] = false; return false; }
            r = p.InverseTransformDirection(e.position - c.position);
            if (r.sqrMagnitude < 1e-12f) return false;
            r.Normalize();
            return true;
        }

        // the swing of E - C from its rest direction, split into the front plane and the side plane (degrees, + = towards her front /
        // the joint's outer side)
        static void Swing(int j, Vector3 r, out float fwd, out float side)
        {
            Vector3 u = _rest[j];
            Vector3 f = (_front[j] - Vector3.Dot(_front[j], u) * u).normalized;
            Vector3 l = (_side[j] - Vector3.Dot(_side[j], u) * u - Vector3.Dot(_side[j], f) * f).normalized;
            float ru = Vector3.Dot(r, u);
            fwd = Mathf.Atan2(Vector3.Dot(r, f), ru) * Mathf.Rad2Deg;
            side = Mathf.Atan2(Vector3.Dot(r, l), ru) * Mathf.Rad2Deg;
        }

        // Fills tension[k] (0..1) for each muscle of the layer from her pose this frame; false when her bones aren't bound.
        public static bool Tension(Character her, MuscleLayer L, float[] tension)
        {
            if (her != _bound || her.Bones.Count != _boundBones) Bind(her);
            CheckPregnancy(her);
            for (int k = 0; k < tension.Length; k++) tension[k] = 0f;
            if (Joints == 0 || L == null) return false;
            Vector3 r;
            // trunk
            Flex = Side = Twist = 0f;
            if (Dir(0, out r)) Swing(0, r, out Flex, out Side);
            SideBend[0] = Sm(5f, 25f, Side); SideBend[1] = Sm(5f, 25f, -Side);   // side bend + = towards her left
            Vector3 up = Vector3.up;
            Vector3 front = Vector3.forward;
            if (_chest != null) front = _chest.TransformDirection(_chestFront);
            if (_chest != null && _root != null && _spine != null && _neck != null)
            {
                Vector3 ax = (_neck.position - _spine.position).normalized;
                Vector3 rf = _root.TransformDirection(_rootFront);
                Vector3 a = Vector3.ProjectOnPlane(front, ax), b = Vector3.ProjectOnPlane(rf, ax);
                if (a.sqrMagnitude > 1e-6f && b.sqrMagnitude > 1e-6f) Twist = Vector3.SignedAngle(b, a, ax);
            }
            float prone = -Vector3.Dot(front, up);     // 1 = her chest faces the floor, -1 = the ceiling
            // 0.11.23 her head on her neck (the neck muscles)
            HeadFlex = HeadSide = HeadTurn = 0f;
            if (_head != null && _neck != null && _chest != null)
            {
                Vector3 hr = _chest.InverseTransformDirection(_head.position - _neck.position);
                if (hr.sqrMagnitude > 1e-10f)
                {
                    hr.Normalize();
                    Vector3 u = _headRest, f = (_chestFront - Vector3.Dot(_chestFront, u) * u).normalized;
                    Vector3 l = (_chestLeft - Vector3.Dot(_chestLeft, u) * u - Vector3.Dot(_chestLeft, f) * f).normalized;
                    float hu = Vector3.Dot(hr, u);
                    HeadFlex = Mathf.Atan2(Vector3.Dot(hr, f), hu) * Mathf.Rad2Deg;
                    HeadSide = Mathf.Atan2(Vector3.Dot(hr, l), hu) * Mathf.Rad2Deg;
                    Vector3 axw = (_head.position - _neck.position).normalized;
                    Vector3 hf = Vector3.ProjectOnPlane(_head.TransformDirection(_headFront), axw), cf = Vector3.ProjectOnPlane(front, axw);
                    if (hf.sqrMagnitude > 1e-6f && cf.sqrMagnitude > 1e-6f) HeadTurn = Vector3.SignedAngle(cf, hf, axw);   // + = toward her right (+x)
                }
            }
            for (int s = 0; s < 2; s++)
            {
                HipFlex[s] = Abd[s] = Knee[s] = Plantar[s] = Elev[s] = ArmSwing[s] = Elbow[s] = Wrist[s] = LegLoad[s] = ArmLoad[s] = 0f;
                if (Dir(1 + s, out r)) Swing(1 + s, r, out HipFlex[s], out Abd[s]);
                if (Dir(3 + s, out r)) Knee[s] = Vector3.Angle(r, _rest[3 + s]);
                if (Dir(5 + s, out r) && _c[5 + s] != null && _p[5 + s] != null)
                {
                    // plantar flexion: the foot turns towards the shin's line (toes down); 0 at the bind pose
                    Vector3 shin = _p[5 + s].InverseTransformDirection(_c[5 + s].position - _p[5 + s].position).normalized;
                    Plantar[s] = Vector3.Angle(_rest[5 + s], _ref[5 + s]) - Vector3.Angle(r, shin);
                }
                if (Dir(7 + s, out r))
                {
                    Elev[s] = Vector3.Angle(r, _ref[7 + s]);                 // 0 hanging, 90 out to the side / forward, 180 overhead
                    float f, sd;
                    Swing(7 + s, r, out f, out sd);
                    ArmSwing[s] = f;                                          // + forward, - behind her
                }
                if (Dir(9 + s, out r)) Elbow[s] = Vector3.Angle(r, _rest[9 + s]);
                if (Dir(11 + s, out r)) Wrist[s] = Vector3.Angle(r, _rest[11 + s]);
                // loads: a leg under her weight (hip well above the ankle), an arm propping her up (chest down, hand below the shoulder)
                Transform hip = _c[1 + s], ank = _c[5 + s], sho = _c[7 + s], wri = _c[11 + s];
                if (hip != null && ank != null) LegLoad[s] = Sm(0.25f, 0.6f, (hip.position.y - ank.position.y) / _legLen);
                if (sho != null && wri != null) ArmLoad[s] = Sm(0.2f, 0.7f, prone) * Sm(0.3f, 0.7f, (sho.position.y - wri.position.y) / _armLen);
            }
            float hipAvg = 0.5f * (HipFlex[0] + HipFlex[1]);
            BackLoad = Sm(0.2f, 0.7f, prone) * Sm(30f, 70f, hipAvg);           // bent over / on all fours (lying flat: hips straight)
            AbsLoad = Sm(0.2f, 0.6f, -prone) * Sm(30f, 70f, hipAvg);           // sitting leaning back (lying on her back: hips straight)
            float ext = -Flex;
            // 0.11.8 anatomy pass (the tension now also drives geometry, so a muscle that is only stretched must not read as contracted):
            // lying on her back with the legs lifted off the floor the abs brace the pelvis (hip flexors pull on it)
            float supine = Sm(0.3f, 0.7f, -prone);
            float legLift = supine * Sm(30f, 80f, hipAvg) * (1f - 0.5f * (LegLoad[0] + LegLoad[1]));
            float absT = Mathf.Max(Mathf.Max(Sm(5f, 35f, Flex), 0.85f * AbsLoad), 0.6f * legLift);
            float backT = Mathf.Max(Mathf.Max(Sm(5f, 30f, ext), BackLoad), 0.5f * Sm(10f, 35f, Mathf.Abs(Twist)));
            float elevMax = Mathf.Max(Elev[0], Elev[1]);
            for (int k = 0; k < L.Count && k < tension.Length; k++)
            {
                string n = L.MuscleName[k];
                int s = L.Side[k] == "R" ? 1 : 0;
                float sgn = s == 0 ? 1f : -1f;          // side bend + = towards her left
                float t;
                if (n == "abs") t = absT;
                else if (n == "ribcage") t = Mathf.Max(Mathf.Max(0.8f * Sm(110f, 165f, elevMax), 0.8f * Sm(5f, 30f, ext)), 0.4f * absT);   // ribs showing (no geometry: GpuContact)
                else if (n == "back") t = backT;
                else if (n.StartsWith("obliques")) t = Mathf.Max(Mathf.Max(0.7f * absT, Sm(8f, 30f, Mathf.Abs(Twist))), 0.9f * Sm(5f, 25f, Side * sgn) + 0.3f * Sm(5f, 25f, Mathf.Abs(Side)));
                else if (n.StartsWith("serratus")) t = Mathf.Max(Mathf.Max(0.9f * Sm(100f, 160f, Elev[s]), 0.5f * ArmLoad[s]), 0.4f * absT);
                // lats extend / adduct the arm: they work propping her up, pulling the arm back and holding the trunk; overhead they are mostly
                // stretched (0.7 -> 0.35: the flare that shows), so a raised arm alone no longer reads as a contracted lat
                else if (n.StartsWith("lats")) t = Mathf.Max(Mathf.Max(0.35f * Sm(100f, 160f, Elev[s]), 0.6f * backT), Mathf.Max(0.8f * ArmLoad[s], 0.7f * Sm(10f, 40f, -ArmSwing[s])));
                else if (n.StartsWith("glutes")) t = Mathf.Max(Mathf.Max(Sm(30f, 100f, HipFlex[s]) * LegLoad[s], Sm(5f, 25f, -HipFlex[s])), Mathf.Max(0.6f * Sm(15f, 40f, Abd[s]), 0.5f * backT));
                else if (n.StartsWith("quads")) t = Mathf.Max(Sm(15f, 95f, Knee[s]) * LegLoad[s], 0.7f * Sm(30f, 80f, HipFlex[s]) * (1f - Sm(30f, 70f, Knee[s])) * (1f - LegLoad[s]));
                // hamstrings: hip extension (with the glutes), knee flexion without her weight on the leg, and holding a hip hinge on a straight
                // loaded leg; a straight leg lifted while she lies (a stretch, 0.10.0 gave it half tension) is passive
                else if (n.StartsWith("hams")) t = Mathf.Max(Mathf.Max(Sm(40f, 110f, HipFlex[s]) * (1f - Sm(20f, 60f, Knee[s])) * LegLoad[s], Sm(40f, 110f, Knee[s]) * (1f - LegLoad[s])),
                                                             0.7f * Sm(5f, 25f, -HipFlex[s]));
                else if (n.StartsWith("adductors")) t = Mathf.Max(Sm(5f, 20f, -Abd[s]), 0.3f * Sm(30f, 60f, Abd[s]) * LegLoad[s]);   // wide: stretched, works only under her weight
                else if (n.StartsWith("calves")) t = Mathf.Max(Sm(5f, 30f, Plantar[s]) * (0.5f + 0.5f * LegLoad[s]), 0.5f * Sm(20f, 80f, Knee[s]) * LegLoad[s]);
                else if (n.StartsWith("deltoids")) t = Mathf.Max(Mathf.Max(Sm(60f, 110f, Elev[s]), 0.6f * ArmLoad[s]), 0.5f * Sm(15f, 45f, Mathf.Abs(ArmSwing[s])));
                else if (n.StartsWith("biceps")) t = Mathf.Max(Sm(30f, 110f, Elbow[s]), 0.3f * ArmLoad[s]);
                else if (n.StartsWith("triceps")) t = Mathf.Max(Sm(10f, 40f, -ArmSwing[s]), ArmLoad[s] * (1f - 0.6f * Sm(30f, 80f, Elbow[s])));
                // 0.11.13: pecs push the arm forward / across and bear weight on the hands; traps lift and brace the shoulders (arms raised,
                // propping her up, an arched or held trunk)
                else if (n.StartsWith("pecs")) t = Mathf.Max(Mathf.Max(Sm(15f, 60f, ArmSwing[s]) * (1f - 0.5f * Sm(100f, 150f, Elev[s])), 0.8f * ArmLoad[s]), 0.3f * absT);
                // 0.11.18: arms pulled back (hands clasped behind her) squeeze the shoulder blades together: traps and rhomboids
                else if (n == "traps") t = Mathf.Max(Mathf.Max(0.8f * Sm(80f, 150f, elevMax), 0.7f * Mathf.Max(ArmLoad[0], ArmLoad[1])),
                                                     Mathf.Max(0.6f * backT, Sm(10f, 40f, -Mathf.Min(ArmSwing[0], ArmSwing[1]))));
                // 0.11.23: the sternocleidomastoid turns the head to the other side, tips it to its own side and, with its partner, tips it
                // forward or lifts it off the floor (a crunch, lying on her back)
                else if (n.StartsWith("neck_")) t = Mathf.Max(Mathf.Max(Sm(10f, 45f, HeadTurn * sgn), Sm(8f, 25f, HeadSide * sgn)),
                                                              Mathf.Max(0.7f * Sm(10f, 35f, HeadFlex), 0.6f * absT));
                else if (n.StartsWith("forearms")) t = Mathf.Max(Mathf.Max(0.6f * Sm(30f, 110f, Elbow[s]), Sm(15f, 50f, Wrist[s])), 0.8f * ArmLoad[s]);
                else t = 0f;
                tension[k] = Mathf.Clamp01(t);
            }
            return true;
        }

        // "trunk flex 12 side -3 twist 5 deg, hips 40/38, knees 85/80, ..." for the peak log
        public static string PoseLine()
        {
            var sb = new StringBuilder();
            sb.Append("trunk flex ").Append(Flex.ToString("0")).Append(" side ").Append(Side.ToString("0")).Append(" twist ").Append(Twist.ToString("0"))
              .Append(" deg, head flex ").Append(HeadFlex.ToString("0")).Append(" side ").Append(HeadSide.ToString("0")).Append(" turn ").Append(HeadTurn.ToString("0"))
              .Append(", hip flex ").Append(HipFlex[0].ToString("0")).Append('/').Append(HipFlex[1].ToString("0"))
              .Append(" abd ").Append(Abd[0].ToString("0")).Append('/').Append(Abd[1].ToString("0"))
              .Append(", knee ").Append(Knee[0].ToString("0")).Append('/').Append(Knee[1].ToString("0"))
              .Append(", plantar ").Append(Plantar[0].ToString("0")).Append('/').Append(Plantar[1].ToString("0"))
              .Append(", arm elev ").Append(Elev[0].ToString("0")).Append('/').Append(Elev[1].ToString("0"))
              .Append(" swing ").Append(ArmSwing[0].ToString("0")).Append('/').Append(ArmSwing[1].ToString("0"))
              .Append(", elbow ").Append(Elbow[0].ToString("0")).Append('/').Append(Elbow[1].ToString("0"))
              .Append(", loads back ").Append(BackLoad.ToString("0.0")).Append(" abs ").Append(AbsLoad.ToString("0.0"))
              .Append(" legs ").Append(LegLoad[0].ToString("0.0")).Append('/').Append(LegLoad[1].ToString("0.0"))
              .Append(" arms ").Append(ArmLoad[0].ToString("0.0")).Append('/').Append(ArmLoad[1].ToString("0.0"));
            return sb.ToString();
        }

        public static void Release()
        {
            MuscleFlex.Clear();
            _bound = null;
            _boundBones = -1;
            Joints = 0;
            for (int j = 0; j < NJ; j++) { _ok[j] = false; _p[j] = _c[j] = _e[j] = null; }
            _chest = _root = _neck = _spine = _head = null;
        }
    }
    // 0.11.8 tensed muscles as geometry: the body sheet's faded activations (Wrinkles.UpdateMuscles publishes them every frame while
    // [Muscles] On), read by GpuContact (Compose pushes each muscle's belly out along the normal; Depth / Compose shrink its soft depth so a
    // flexed muscle dents less) and by the hands' resistance (HerBody.SkinAllowance, SoftSim's zone limit: the same firmness on the CPU).
    internal static class MuscleFlex
    {
        public const int Max = 32;                         // = Wrinkles.MaxRegions, the shader's float4 [8] arrays
        public static MuscleLayer Layer;                   // the layout the activations belong to (null: none published)
        public static readonly float[] Act = new float[Max];
        public static float Firm = 0.5f;                   // [Muscles] Firmness, set by Body every frame: soft depth x (1 - Firm x activation)
        static int _frame = -100;
        static MuscleLayer _mapFor;
        static readonly Dictionary<string, int[]> _parts = new Dictionary<string, int[]>();

        public static void Publish(MuscleLayer L, float[] act)
        {
            if (L == null || act == null) return;
            Layer = L;
            int n = Math.Min(Math.Min(L.Count, act.Length), Max);
            for (int k = 0; k < Max; k++) Act[k] = k < n ? Mathf.Clamp01(act[k]) : 0f;
            _frame = Time.frameCount;
        }

        // the activations are this frame's (or at most a few frames old: the muscles stop publishing when switched off or her sheet goes)
        public static bool Live { get { return Layer != null && Time.frameCount - _frame <= 3; } }

        // 0.11.8 Flex test (panel): each area in turn at full activation for TestStep s over whatever she is playing, the others at 0, so
        // the geometry can be checked apart from the pose's tension (works with the muscle relief off too: only GPU contact is needed)
        public static readonly string[] TestAreas = { "arms", "back", "core", "legs" };
        public const float TestStep = 2f;
        static float _testStart = -1f;
        public static void StartTest() { _testStart = Time.time; }
        public static void StopTest() { _testStart = -1f; }
        // the area under test now (null: no test running); the test ends by itself after the last area
        public static string TestArea()
        {
            if (_testStart < 0f) return null;
            int i = (int)((Time.time - _testStart) / TestStep);
            if (i < 0 || i >= TestAreas.Length) { _testStart = -1f; return null; }
            return TestAreas[i];
        }
        public static float TestLeft(string area)
        {
            int i = Array.IndexOf(TestAreas, area);
            return i < 0 || _testStart < 0f ? 0f : Mathf.Max(0f, _testStart + (i + 1) * TestStep - Time.time);
        }

        public static void Clear()
        {
            Layer = null;
            _frame = -100;
            for (int k = 0; k < Max; k++) Act[k] = 0f;
        }

        // The CPU side of the firmness: 1 - Firm x the strongest activation of the muscles under a body part (HerBody segment names
        // "upperarm_L", "thigh_R", "spine" ... and SoftZones ids "glute_L", "thighOut_R", "belly"); 1 when nothing is live.
        public static float SoftFactor(string part)
        {
            if (part == null || !Live || !(Firm > 0f)) return 1f;
            if (_mapFor != Layer) { _parts.Clear(); _mapFor = Layer; }
            int[] ids;
            if (!_parts.TryGetValue(part, out ids)) { ids = MusclesOf(Layer, part); _parts[part] = ids; }
            float a = 0f;
            for (int i = 0; i < ids.Length; i++) if (Act[ids[i]] > a) a = Act[ids[i]];
            return 1f - Mathf.Clamp01(Firm) * a;
        }

        static int[] MusclesOf(MuscleLayer L, string part)
        {
            string side = part.EndsWith("_L", StringComparison.Ordinal) ? "_L" : part.EndsWith("_R", StringComparison.Ordinal) ? "_R" : "";
            string b = side.Length > 0 ? part.Substring(0, part.Length - 2) : part;
            string[] want;
            switch (b)
            {
                case "upperarm": want = new[] { "biceps", "triceps", "deltoids" }; break;
                case "forearm": want = new[] { "forearms" }; break;
                case "thigh": want = new[] { "quads", "hams", "adductors" }; break;
                case "thighOut": want = new[] { "quads" }; break;
                case "thighIn": want = new[] { "adductors" }; break;
                case "calf": want = new[] { "calves" }; break;
                case "butt": case "glute": want = new[] { "glutes" }; break;
                case "spine": case "belly": want = new[] { "abs", "obliques_L", "obliques_R" }; side = ""; break;
                case "chest": want = new[] { "pecs_L", "pecs_R" }; side = ""; break;           // 0.11.13
                case "scapula": want = new[] { "traps", "lats_L", "lats_R" }; side = ""; break;
                case "neck": want = new[] { "neck_L", "neck_R", "traps" }; side = ""; break;   // 0.11.23
                default: return new int[0];   // breasts, head, hands, feet: no authored muscle under them
            }
            var res = new List<int>();
            for (int k = 0; k < L.Count && k < Max; k++)
                for (int i = 0; i < want.Length; i++)
                    if (L.MuscleName[k] == want[i] + side) res.Add(k);
            return res.ToArray();
        }
    }
}
