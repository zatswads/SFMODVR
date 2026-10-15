using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // A contact capsule for the soft zones: endpoints in T's local space (so it follows T), radius in metres.
    // Kind: "vr" (the user's tracked hands/controllers), "partner" (Player/BadGuy hands), "thighL", "thighR", "forearm", "hand" (hers).
    internal struct SoftCapsule
    {
        public Transform T;
        public Vector3 LocalA, LocalB;
        public float Radius;
        public string Owner, Kind;
        public CapSource Src;   // 0.7.7: who this capsule belongs to (shared by its capsules); null = always live, ungated
    }

    // 0.7.7: one thing that can touch her (a VR hand, a physics hand, a partner rig, her own body). Gated sources count only while one
    // of their renderers is drawn in the headset (VrHands.UpdateLive, once per frame): a hidden partner whose animation still puts his
    // hands on her, or controller models replaced by the physics hands, must not dent her or push her breast physics.
    internal sealed class CapSource
    {
        public int Id;                            // 1..VrHands.MaxSources-1, stable while the source lives (0 = no source)
        public string Name;                       // "your left hand", "your right physics hand", "Player", "[Character]", "her"
        public string Kind;                       // "vr", "partner", "hers"
        public bool Gated;                        // false: always live (her own capsules, the physics hands)
        public bool Live = true;                  // written by VrHands.UpdateLive
        public string Why = "";                   // why not live ("hidden"), or a note ("no renderer found: assumed drawn")
        public readonly List<Renderer> Rends = new List<Renderer>();
        public int CapCount;
    }

    // Finds the hands that can touch her (the user's VR hands/controllers, the partner rigs' hands, her own forearms/hands/thighs)
    // and keeps them as SoftCapsules; logs a one-time rig survey; mirrors the vr and partner capsules into the game's own
    // breast DynamicBones. All Refresh work happens every 2 s and is cheap while nothing changed; nothing runs per frame.
    internal static class VrHands
    {
        public static readonly List<SoftCapsule> Capsules = new List<SoftCapsule>();
        public static int Version;                               // bumped whenever Capsules changes
        public static Transform LeftController, RightController; // the tracked controller poses, null if not found
        public static string Status = "";

        // ---- 0.7.7 capsule sources (interface; see CapSource) ----
        public const int MaxSources = 64;
        public static bool GateHidden = true;     // [Physics] GateHidden: false = every source live (to compare)
        public static int BreastColliders = 1;    // [Physics] BreastColliders: 0 off, 1 main breast chains, 2 all breast DynamicBones
        public static readonly List<CapSource> Sources = new List<CapSource>();
        static int _nextSourceId = 1;

        // A new source with the next free id (ids wrap within 1..MaxSources-1, skipping ones still in Sources).
        public static CapSource NewSource(string name, string kind, bool gated)
        {
            int id = 0;
            for (int n = 0; n < MaxSources && id == 0; n++)
            {
                int cand = _nextSourceId;
                _nextSourceId = _nextSourceId + 1 >= MaxSources ? 1 : _nextSourceId + 1;
                bool used = false;
                foreach (var s in Sources) if (s.Id == cand) { used = true; break; }
                if (!used) id = cand;
            }
            var src = new CapSource { Id = id, Name = name, Kind = kind, Gated = gated };
            Sources.Add(src);
            return src;
        }

        public static void DropSource(CapSource s) { if (s != null) Sources.Remove(s); }

        // ---- visibility gate ----

        static Camera[] _camBuf = new Camera[32];   // GetAllCameras fills this: grown only when there are more cameras
        static int _camMask = -1;                            // the culling mask of the camera the user looks through (-1: everything)
        static float _camNext;
        static string _camLogged = "";
        static bool _statusDirty;                            // a source went hidden/drawn: Status is rebuilt at the end of UpdateLive
        static readonly Predicate<Renderer> NullRend = r => r == null;

        // Once per frame, before the soft points: Live for every gated source = one of its renderers is drawn in the headset.
        // Only cached renderer lists are read (no scene queries); the camera mask is refreshed every 2 s. Why not Renderer.isVisible:
        // it is true for shadow-only / off-screen shadow casters and false for frustum-culled bounds of ANY camera set (the desktop
        // mirror, probes), so "drawn in the headset" is checked explicitly: enabled, active, drawn (not shadows only), on a layer
        // the headset camera renders.
        public static void UpdateLive()
        {
            float now = Time.unscaledTime;
            if (now >= _camNext) { _camNext = now + 2f; RefreshCameraMask(); }
            for (int i = 0; i < Sources.Count; i++)
            {
                var s = Sources[i];
                if (!s.Gated) continue;   // her own capsules and the physics hands (their sources): always live
                bool live = true;
                string why = "";
                if (GateHidden)
                {
                    int seen = 0;
                    live = false;
                    var rl = s.Rends;
                    for (int k = 0; k < rl.Count; k++)
                    {
                        var r = rl[k];
                        if (r == null) continue;
                        seen++;
                        if (r.enabled && r.gameObject.activeInHierarchy && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly
                            && !r.forceRenderingOff && ((_camMask >> r.gameObject.layer) & 1) != 0) { live = true; break; }
                    }
                    if (seen == 0) { live = true; why = "no renderer found: assumed drawn"; }   // nothing to judge by: stay as before 0.7.7
                    else if (!live) why = "hidden";
                }
                s.Why = why;
                if (live == s.Live) continue;
                s.Live = live;
                _statusDirty = true;
                Plugin.Log.LogInfo(live ? "VrHands: " + s.Name + " is drawn again"
                                        : "VrHands: " + s.Name + " is hidden: its " + s.CapCount + " capsules don't touch her");
            }
            if (_statusDirty) { _statusDirty = false; BuildStatus(); }
        }

        // The layers the headset draws: the union of the cullingMasks of every enabled stereo camera (the eye cameras; an overlay/UI stereo
        // camera or a second camera drawing the hands on their own layer only adds layers, so a union can never call a drawn hand hidden),
        // plus Camera.main; no camera found = no layer filtering. Refreshed every 2 s.
        static void RefreshCameraMask()
        {
            int count = Camera.allCamerasCount;
            if (_camBuf.Length < count) _camBuf = new Camera[Mathf.Max(count, _camBuf.Length * 2)];   // GetAllCameras throws on a short buffer
            int n = Camera.GetAllCameras(_camBuf);
            int mask = 0, stereo = 0;
            string first = null;
            for (int i = 0; i < n; i++)
            {
                var c = _camBuf[i];
                if (c == null || !c.enabled || !c.stereoEnabled || !c.gameObject.activeInHierarchy) continue;
                mask |= c.cullingMask; stereo++;
                if (first == null) first = c.name;
            }
            for (int i = 0; i < _camBuf.Length; i++) _camBuf[i] = null;   // don't keep cameras alive
            var main = Camera.main;
            if (main != null) { mask |= main.cullingMask; if (first == null) first = main.name; }
            _camMask = first != null ? mask : -1;
            string key = (first ?? "none") + "|" + stereo + "|" + _camMask;
            if (key == _camLogged) return;
            _camLogged = key;
            Plugin.Log.LogInfo("VrHands: visibility against " + (first == null ? "no camera (no layer filter)" : stereo + " stereo camera(s) + main, first '" + first + "'")
                               + " (cullingMask 0x" + _camMask.ToString("X8") + ")");
        }

        // What else the contacts affect (the answer to "why did that move?"); logged once at the first Rebuild, shown under Status.
        static string _feed;
        static int _feedMode = -1;
        public static string ContactsFeed
        {
            get
            {
                int m = BreastColliders;
                if (_feed == null || _feedMode != m)
                {
                    _feedMode = m;
                    string chains = m <= 0 ? "off" : m == 1 ? "main chains" : "all breast chains";
                    _feed = "Contacts feed: soft points (dent, jiggle, resist): your hands, partner, her own forearms/hands/thighs | per-vertex GPU imprint: your hands, partner"
                            + " | the game's breast physics (" + chains + "): your hands, partner | physics hands' force cap and limb push: your physics hands only"
                            + " | secondary motion fade: physics hands on her limbs, soft points on thighs/glutes/belly. Hidden hands feed none of these.";
                }
                return _feed;
            }
        }

        // Once per frame, after the soft points: attach/detach the breast DynamicBone mirror colliders whose source changed liveness.
        // Removing the collider from m_Colliders is what certainly works; Behaviour.enabled is set too (harmless if the game's
        // DynamicBone version doesn't look at it).
        public static void MirrorLive()
        {
            if (_mirrors.Count == 0) return;
            _liveLogged.Clear();
            for (int i = 0; i < _mirrors.Count; i++)
            {
                var m = _mirrors[i];
                if (m.Src == null || !m.Src.Gated || m.Src.Live == m.Attached) continue;
                m.Attached = m.Src.Live;
                if (m.Col == null) continue;
                var beh = m.Col as Behaviour;
                if (beh != null) beh.enabled = m.Attached;
                if (m.Attached)
                {
                    for (int k = 0; k < _dbLists.Count; k++)
                        if (!_dbLists[k].Contains(m.Col)) { _dbLists[k].Add(m.Col); _adds.Add(new KeyValuePair<IList, object>(_dbLists[k], m.Col)); }
                }
                else
                {
                    for (int k = 0; k < _dbLists.Count; k++) { try { _dbLists[k].Remove(m.Col); } catch (Exception) { } }
                    for (int k = _adds.Count - 1; k >= 0; k--) if (ReferenceEquals(_adds[k].Value, m.Col)) _adds.RemoveAt(k);
                }
                if (!_liveLogged.Contains(m.Src))
                {
                    _liveLogged.Add(m.Src);
                    Plugin.Log.LogInfo("VrHands: breast colliders of " + m.Src.Name + (m.Attached ? " attached again (drawn)" : " detached (hidden)"));
                }
            }
        }
        static readonly List<CapSource> _liveLogged = new List<CapSource>();

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // Sizes (metres): the design's radii.
        // PalmThick: the capsule radius is half of a 3 cm thick palm (0.7.6's 3.5 cm palms stuck 2 cm out of the hand on both faces).
        internal const float PalmThick = 0.015f;
        const float VrFinger = 0.011f, VrController = 0.045f;
        const float PartnerFinger = 0.010f;
        const float HerThigh = 0.06f, HerForearm = 0.030f, HerHand = 0.022f;

        // ---- game types (Assembly-CSharp isn't referenced) ----

        static bool _typesLooked;
        static Type _hand, _process, _player, _camSys, _db, _dbc;
        static FieldInfo _fRoot, _fColliders;

        static void Types()
        {
            if (_typesLooked) return;
            _typesLooked = true;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = a.GetName().Name;
                if (n != "Assembly-CSharp" && n != "Assembly-CSharp-firstpass") continue;
                _hand = _hand ?? a.GetType("VR_HandController");
                _process = _process ?? a.GetType("VR_GameProcess_System");
                _player = _player ?? a.GetType("PlayerController");
                _camSys = _camSys ?? a.GetType("Camera_system");
                _db = _db ?? a.GetType("DynamicBone");
                _dbc = _dbc ?? a.GetType("DynamicBoneCollider");
            }
            if (_db != null)
            {
                _fRoot = _db.GetField("m_Root", Any);
                _fColliders = _db.GetField("m_Colliders", Any);
                if (_fRoot == null || _fColliders == null) _db = null;
            }
            Plugin.Log.LogInfo("VrHands: " + (_hand != null ? "VR_HandController" : "no VR_HandController")
                               + (_process != null ? ", VR_GameProcess_System" : "") + (_db != null && _dbc != null ? ", DynamicBone+Collider" : ", no DynamicBone (game breast colliders off)"));
        }

        static object Get(object o, string name)
        {
            if (o == null) return null;
            var uo = o as UnityEngine.Object;
            if (!ReferenceEquals(uo, null) && uo == null) return null; // destroyed
            var t = o.GetType();
            var f = t.GetField(name, Any);
            if (f != null) return f.GetValue(o);
            var p = t.GetProperty(name, Any);
            return p != null && p.GetIndexParameters().Length == 0 ? p.GetValue(o, null) : null;
        }

        static void Set(object o, string f, object v)
        {
            var fi = o.GetType().GetField(f, Any);
            if (fi == null) return;
            if (fi.FieldType.IsEnum && !(v is Enum)) v = Enum.ToObject(fi.FieldType, v);
            fi.SetValue(o, v);
        }

        // ---- what we keep ----

        sealed class Partner
        {
            public Transform Root;
            public readonly List<SoftCapsule> Caps = new List<SoftCapsule>();
            public float NextTry;   // an empty rig (not loaded yet?) is looked at again later, not every call
            public int LoggedCount = -1;
            public CapSource Src;   // null while it has no capsules
            public int Skipped;     // hands of a rig nested inside this root (Player/BadGuy under "[Character]") that belong to that rig
            public string SkippedOwners = "";
            public bool Dirty;      // a rig appeared inside this root: build again so it stops holding that rig's hands
        }

        static readonly List<SoftCapsule> _vrL = new List<SoftCapsule>(), _vrR = new List<SoftCapsule>();
        static CapSource _srcL, _srcR;
        static string _vrSrcL = "", _vrSrcR = "";   // how each side was found, for Status
        static float _vrNext;
        static readonly List<Partner> _partners = new List<Partner>();
        static readonly List<SoftCapsule> _hers = new List<SoftCapsule>();
        static CapSource _hersSrc;
        static Transform _hersRoot;
        static bool _surveyed, _feedLogged;
        static readonly Predicate<SoftCapsule> Dead = c => c.T == null;

        // Every 2 s while physics or embody is on: look for hands that appeared, drop what vanished, rebuild Capsules on any change.
        public static void Refresh(Character her, List<Character> everyone)
        {
            Types();
            bool changed = false;
            changed |= DropDead();
            changed |= RefreshVr();
            changed |= RefreshPartners(her, everyone);
            changed |= RefreshHers(her);
            if (LeftController == null) LeftController = null;     // a destroyed controller reads as null: make it really null
            if (RightController == null) RightController = null;
            for (int i = 0; i < Sources.Count; i++) if (Sources[i].Gated) Sources[i].Rends.RemoveAll(NullRend);   // renderers destroyed since
            if (changed) Rebuild();
        }

        // Capsules whose bone is gone. A rig that lost some of its capsules is rebuilt whole next time (its bones were swapped).
        static bool DropDead()
        {
            bool ch = false;
            if (_vrL.RemoveAll(Dead) > 0) { _vrL.Clear(); LeftController = null; DropSource(_srcL); _srcL = null; ch = true; }
            if (_vrR.RemoveAll(Dead) > 0) { _vrR.Clear(); RightController = null; DropSource(_srcR); _srcR = null; ch = true; }
            if (_hers.RemoveAll(Dead) > 0) { _hers.Clear(); _hersRoot = null; DropSource(_hersSrc); _hersSrc = null; ch = true; }
            for (int i = _partners.Count - 1; i >= 0; i--)
            {
                var p = _partners[i];
                if (p.Root == null || p.Caps.RemoveAll(Dead) > 0) { DropSource(p.Src); _partners.RemoveAt(i); ch = true; }
            }
            return ch;
        }

        static void Rebuild()
        {
            Capsules.Clear();
            Capsules.AddRange(_vrL);
            Capsules.AddRange(_vrR);
            foreach (var p in _partners) Capsules.AddRange(p.Caps);
            Capsules.AddRange(_hers);
            BuildStatus();
            Version++;
            Plugin.Log.LogInfo("VrHands: " + Status + " (" + Capsules.Count + " capsules, version " + Version + ")");
            if (!_feedLogged) { _feedLogged = true; Plugin.Log.LogInfo("VrHands: " + ContactsFeed); }
        }

        // "your hands: ...; partner: Player 42, BadGuy 42 (hidden), [Character] 36 (4 hands shared with Player/BadGuy skipped); hers 6".
        // Rebuilt when the capsules change and when a source goes hidden/drawn.
        static void BuildStatus()
        {
            var sb = new StringBuilder("your hands: ");
            if (_vrL.Count + _vrR.Count == 0) sb.Append("none found");
            else sb.Append(_vrL.Count == 0 ? _vrSrcR : (_vrR.Count == 0 || _vrSrcL == _vrSrcR ? _vrSrcL : _vrSrcL + " + " + _vrSrcR))
                   .Append(" (left ").Append(_vrL.Count).Append(" capsules").Append(HiddenNote(_srcL)).Append(", right ").Append(_vrR.Count).Append(HiddenNote(_srcR)).Append(')');
            sb.Append("; partner: ");
            int np = 0;
            foreach (var p in _partners)
            {
                if (np++ > 0) sb.Append(", ");
                sb.Append(p.Root != null ? p.Root.name : "?").Append(' ').Append(p.Caps.Count);
                bool hid = p.Src != null && !p.Src.Live;
                if (hid || p.Skipped > 0)
                {
                    sb.Append(" (");
                    if (hid) sb.Append("hidden");
                    if (hid && p.Skipped > 0) sb.Append("; ");
                    if (p.Skipped > 0) sb.Append(p.Skipped).Append(" hands shared with ").Append(p.SkippedOwners).Append(" skipped");
                    sb.Append(')');
                }
            }
            if (np == 0) sb.Append("none");
            sb.Append("; hers ").Append(_hers.Count);
            Status = sb.ToString();
        }

        static string HiddenNote(CapSource s) { return s != null && !s.Live ? " hidden" : ""; }

        // Every capsule of a list belongs to `src` (struct copies share the reference); the source remembers how many it has.
        static void Stamp(List<SoftCapsule> caps, CapSource src)
        {
            for (int i = 0; i < caps.Count; i++) { var c = caps[i]; c.Src = src; caps[i] = c; }
            src.CapCount = caps.Count;
        }

        // The renderers that show a VR hand: the model's renderers minus line pointers and UI (the same filter as PhysicsHands hiding
        // the controller models, so the "vr" capsules go dead exactly when the physics hands replace the models) and our own meshes.
        static void CollectRends(CapSource src, Transform root)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                if (r != null && !(r is LineRenderer) && !IsUi(r.transform) && !UnderSf(r.transform)) src.Rends.Add(r);
        }

        // ---- capsule helpers ----

        static void Add(List<SoftCapsule> into, Transform t, Vector3 a, Vector3 b, float r, string owner, string kind)
        {
            if (t == null || (b - a).sqrMagnitude < 1e-8f) return;
            into.Add(new SoftCapsule { T = t, LocalA = t.InverseTransformPoint(a), LocalB = t.InverseTransformPoint(b), Radius = r, Owner = owner, Kind = kind });
        }

        // Our own objects (SfPhys_* hand visuals, SfSoft_* helpers) are never a rig of someone else: a transform that is, or sits under,
        // one whose name starts with "Sf".
        internal static bool UnderSf(Transform t)
        {
            for (var p = t; p != null; p = p.parent) if (p.name.StartsWith("Sf", StringComparison.Ordinal)) return true;
            return false;
        }

        // Anything on a UI canvas is not a hand (0.6.3 matched 'HandJob_Panel'/'handJob' in [VR Mode]). A Canvas always sits on a
        // RectTransform, so one RectTransform on the way up is enough (the UI module itself isn't referenced).
        static bool IsUi(Transform t)
        {
            for (var p = t; p != null; p = p.parent) if (p is RectTransform) return true;
            return false;
        }

        static readonly string[] FingerWords = { "finger", "thumb", "index", "middle", "pinky", "little" };

        static bool IsFingerName(string raw)
        {
            var n = raw.ToLowerInvariant();
            foreach (var w in FingerWords) if (n.Contains(w)) return true;
            // "ring" is also inside spring/string/earring: only where a word starts (start, after a separator, or capital R in CamelCase).
            for (int i = n.IndexOf("ring", StringComparison.Ordinal); i >= 0; i = n.IndexOf("ring", i + 1, StringComparison.Ordinal))
                if (i == 0 || !char.IsLetter(n[i - 1]) || char.IsUpper(raw[i])) return true;
            return false;
        }

        // Finger chains below a hand bone: each root is a finger-named bone whose parent isn't; a group node holding several
        // fingers ("Fingers") is skipped in favour of its children. Segments are bone -> next bone (null: extend the last one).
        static void FingerRoots(Transform hand, List<Transform> roots)
        {
            for (int i = 0; i < hand.childCount; i++)
            {
                var c = hand.GetChild(i);
                if (IsUi(c) || c.name.StartsWith("Sf", StringComparison.Ordinal)) continue;
                if (IsFingerName(c.name))
                {
                    int fingerKids = 0;
                    for (int k = 0; k < c.childCount; k++) if (IsFingerName(c.GetChild(k).name)) fingerKids++;
                    if (fingerKids >= 3) FingerRoots(c, roots); else roots.Add(c);
                }
                else if (c.childCount > 0) FingerRoots(c, roots);   // fingers one level down (palm/metacarpal bone)
            }
        }

        static void ChainSegs(Transform root, List<KeyValuePair<Transform, Transform>> segs)
        {
            var t = root;
            for (int guard = 0; guard < 6 && t != null; guard++)
            {
                Transform next = null; int most = -1;
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (IsUi(c) || c.name.StartsWith("Sf", StringComparison.Ordinal)) continue;
                    int cnt = c.GetComponentsInChildren<Transform>(true).Length + (IsFingerName(c.name) ? 1000 : 0);
                    if (cnt > most) { most = cnt; next = c; }
                }
                if (next != null && !IsFingerName(next.name) && t.childCount != 1) next = null;   // a branch that isn't the finger continuing
                segs.Add(new KeyValuePair<Transform, Transform>(t, next));
                t = next;
            }
        }

        static void SegCaps(List<SoftCapsule> into, List<KeyValuePair<Transform, Transform>> segs, float r, string owner, string kind)
        {
            foreach (var s in segs)
            {
                var t = s.Key;
                Vector3 a = t.position, b;
                if (s.Value != null) b = s.Value.position;
                else if (t.parent != null && (a - t.parent.position).sqrMagnitude > 1e-8f) b = a + (a - t.parent.position) * 0.8f;   // the tip: continue the last bone
                else continue;
                if ((b - a).sqrMagnitude > 0.08f * 0.08f) b = a + (b - a).normalized * 0.08f;   // not a finger
                Add(into, t, a, b, r, owner, kind);
            }
        }

        // A hand without a humanoid avatar: finger chains found by name under `hand`; the palm is up to three PalmThick capsules from
        // the wrist to the roots of the fingers that span it (the two roots farthest apart, plus the one nearest their midpoint; the
        // thumb doesn't count), stored on the common ancestor of the finger roots; no fingers: the wrist forwards. Returns the finger count.
        static int NonHumanoidHand(List<SoftCapsule> into, Transform hand, float fingerR, string owner, string kind)
        {
            var roots = new List<Transform>();
            FingerRoots(hand, roots);
            var segs = new List<KeyValuePair<Transform, Transform>>();
            foreach (var r in roots) ChainSegs(r, segs);
            Transform palm = hand;
            if (roots.Count > 0)
            {
                palm = roots[0].parent;
                foreach (var r in roots) while (palm != null && palm != hand && !r.IsChildOf(palm)) palm = palm.parent;
                if (palm == null) palm = hand;
                var four = new List<Transform>();
                foreach (var r in roots) if (!Body.Norm(r.name).Contains("thumb")) four.Add(r);
                if (four.Count == 0) four.AddRange(roots);
                var pick = new List<Transform>();
                if (four.Count <= 2) pick.AddRange(four);
                else
                {
                    int ia = 0, ib = 1; float far = -1f;
                    for (int i = 0; i < four.Count; i++)
                        for (int j = i + 1; j < four.Count; j++)
                        {
                            float d = (four[i].position - four[j].position).sqrMagnitude;
                            if (d > far) { far = d; ia = i; ib = j; }
                        }
                    Vector3 mid = (four[ia].position + four[ib].position) * 0.5f;
                    int ic = -1; float near = float.MaxValue;
                    for (int i = 0; i < four.Count; i++)
                    {
                        if (i == ia || i == ib) continue;
                        float d = (four[i].position - mid).sqrMagnitude;
                        if (d < near) { near = d; ic = i; }
                    }
                    pick.Add(four[ia]); pick.Add(four[ib]);
                    if (ic >= 0) pick.Add(four[ic]);
                }
                foreach (var r in pick) Add(into, palm, hand.position, r.position, PalmThick, owner, kind);
            }
            else Add(into, palm, palm.position, hand.position + hand.rotation * Vector3.up * 0.08f, PalmThick, owner, kind);   // guess: no finger bones to aim at
            SegCaps(into, segs, fingerR, owner, kind);
            return roots.Count;
        }

        static readonly HumanBodyBones[] KnucklesL = { HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftLittleProximal };
        static readonly HumanBodyBones[] KnucklesR = { HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightLittleProximal };

        // A humanoid avatar's hand: the palm as three PalmThick capsules wrist -> index / middle / little proximal (two would leave a 4 mm
        // valley along the middle finger on an 8 cm palm), and the 15 finger segments (HumanBodyBones thumb..little x 3).
        static int HumanoidHand(List<SoftCapsule> into, Animator anim, bool left, float fingerR, string owner, string kind)
        {
            var hand = anim.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (hand == null) return 0;
            int palm = 0;
            foreach (var kb in left ? KnucklesL : KnucklesR)
            {
                var k = anim.GetBoneTransform(kb);
                if (k == null) continue;
                Add(into, hand, hand.position, k.position, PalmThick, owner, kind);
                palm++;
            }
            if (palm == 0) Add(into, hand, hand.position, hand.position + hand.rotation * Vector3.up * 0.08f, PalmThick, owner, kind);
            var segs = new List<KeyValuePair<Transform, Transform>>();
            int first = (int)(left ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal);
            for (int f = 0; f < 5; f++)
                for (int j = 0; j < 3; j++)
                {
                    var t = anim.GetBoneTransform((HumanBodyBones)(first + f * 3 + j));
                    var next = j < 2 ? anim.GetBoneTransform((HumanBodyBones)(first + f * 3 + j + 1)) : null;
                    if (t != null) segs.Add(new KeyValuePair<Transform, Transform>(t, next));
                }
            SegCaps(into, segs, fingerR, owner, kind);
            return segs.Count;
        }

        // ---- 1-3: the user's hands ----

        static bool RefreshVr()
        {
            if (_vrL.Count > 0 && _vrR.Count > 0) return false;
            if (Time.unscaledTime < _vrNext) return false;
            _vrNext = Time.unscaledTime + 4f;   // still missing a hand: try again, but not on every call
            bool ch = false;
            // 1. the game's own hand models
            if (_hand != null)
            {
                var hc = UnityEngine.Object.FindObjectOfType(_hand);
                if (hc != null)
                {
                    if (_vrL.Count == 0) ch |= VrFromAnimator(Get(hc, "controller_left") as Animator, true);
                    if (_vrR.Count == 0) ch |= VrFromAnimator(Get(hc, "controller_right") as Animator, false);
                }
            }
            // 2. XR Interaction Toolkit controllers under [VR Mode]
            if (_vrL.Count == 0 || _vrR.Count == 0) ch |= VrFromController();
            return ch;
        }

        static bool VrFromAnimator(Animator a, bool left)
        {
            if (a == null || IsUi(a.transform) || UnderSf(a.transform)) return false;
            var caps = left ? _vrL : _vrR;
            string owner = "VR_HandController." + (left ? "controller_left" : "controller_right");
            string how;
            int fingers;
            if (a.isHuman && a.avatar != null)
            {
                fingers = HumanoidHand(caps, a, left, VrFinger, owner, "vr");
                how = "humanoid";
            }
            else
            {
                // bones named like fingers anywhere under the animator (the hand bone is their common ancestor)
                var roots = new List<Transform>();
                FingerRoots(a.transform, roots);
                if (roots.Count > 0)
                {
                    fingers = NonHumanoidHand(caps, a.transform, VrFinger, owner, "vr");
                    how = "finger bones by name";
                }
                else
                {
                    fingers = 0;
                    how = MeshCapsule(caps, a.transform, owner) ? "mesh bounds (no finger bones)" : "nothing usable";
                }
            }
            if (caps.Count == 0) { Plugin.Log.LogInfo("VrHands: VR_HandController " + (left ? "left" : "right") + " '" + a.name + "' gave no capsules (" + how + ")"); return false; }
            // The tracked node: an ancestor carrying a tracking component, else one named like an XR controller, else the animator itself.
            string src;
            var tracked = TrackedAncestor(a.transform, left, out src);
            if (tracked == null) { tracked = a.transform; src = "hand model (offsets may need zeroing)"; }
            if (left) { LeftController = tracked; _vrSrcL = "VR_HandController"; } else { RightController = tracked; _vrSrcR = "VR_HandController"; }
            var csrc = NewSource("your " + (left ? "left" : "right") + " hand", "vr", true);
            CollectRends(csrc, a.transform);
            Stamp(caps, csrc);
            if (left) { DropSource(_srcL); _srcL = csrc; } else { DropSource(_srcR); _srcR = csrc; }
            Plugin.Log.LogInfo("VrHands: your " + (left ? "left" : "right") + " hand from VR_HandController." + (left ? "controller_left" : "controller_right")
                               + " '" + PathOf(a.transform) + "': " + how + ", " + caps.Count + " capsules (" + fingers + " finger segments); tracked node '"
                               + PathOf(tracked) + "' [" + src + "], " + csrc.Rends.Count + " renderers");
            return true;
        }

        // The type name of a component on t that marks it as the tracked node (TrackedPoseDriver, XRController, ActionBasedController),
        // or null. Looked at on detection only, never per frame.
        static string TrackerType(Transform t)
        {
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) continue;
                string n = c.GetType().Name;
                if (n.Contains("TrackedPoseDriver") || n.Contains("XRController") || n.Contains("ActionBasedController")) return n;
            }
            return null;
        }

        // The XR controller above the hand model: an ancestor (within 6 levels) with a tracking component first, else one named
        // like a controller ("LeftHand Controller", "Left Controller"). src says which.
        static Transform TrackedAncestor(Transform t, bool left, out string src)
        {
            int n = 0;
            for (var p = t.parent; p != null && n < 6; p = p.parent, n++)
            {
                if (p is RectTransform) continue;
                string ty = TrackerType(p);
                if (ty != null) { src = "tracked node (" + ty + ")"; return p; }
            }
            n = 0;
            for (var p = t.parent; p != null && n < 4; p = p.parent, n++)
                if (IsControllerName(p.name, left) && !(p is RectTransform)) { src = "named controller"; return p; }
            src = "";
            return null;
        }

        static bool IsControllerName(string name, bool left)
        {
            var n = Body.Norm(name);
            return left ? n.StartsWith("lefthandcontroller") || n.StartsWith("leftcontroller") : n.StartsWith("righthandcontroller") || n.StartsWith("rightcontroller");
        }

        // One capsule along the longest axis of the biggest renderer's bounds under t (a hand model without fingers).
        static bool MeshCapsule(List<SoftCapsule> into, Transform t, string owner)
        {
            Renderer best = null; float most = 0f;
            foreach (var r in t.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer || r is MeshRenderer) || IsUi(r.transform)) continue;
                var s = r.bounds.size;
                float v = s.x * s.y * s.z;
                if (best == null || v > most) { best = r; most = v; }
            }
            if (best == null) return false;
            var bb = best.bounds;
            var e = bb.extents;
            int axis = e.x >= e.y && e.x >= e.z ? 0 : (e.y >= e.z ? 1 : 2);
            float others = (e.x + e.y + e.z - e[axis]) * 0.5f;   // mean half-size of the two short axes
            float r0 = Mathf.Clamp(others, 0.02f, 0.03f);   // 0.7.6 allowed 9 cm: a blob that was never a hand
            var dir = Vector3.zero; dir[axis] = 1f;
            float half = Mathf.Max(e[axis] - r0, 0.005f);
            Add(into, best.transform, bb.center - dir * half, bb.center + dir * half, r0, owner, "vr");
            return into.Count > 0;
        }

        static bool VrFromController()
        {
            if (_process == null) return false;
            var proc = UnityEngine.Object.FindObjectOfType(_process);
            var vr = Get(proc, "VR_Mode") as GameObject;
            if (vr == null) return false;
            Transform left = null, right = null;
            int dl = 99, dr = 99;
            foreach (var t in vr.GetComponentsInChildren<Transform>(true))
            {
                if (IsUi(t) || UnderSf(t)) continue;
                int d = 0; for (var p = t; p != vr.transform && p != null; p = p.parent) d++;
                if (_vrL.Count == 0 && IsControllerName(t.name, true) && d < dl) { left = t; dl = d; }
                if (_vrR.Count == 0 && IsControllerName(t.name, false) && d < dr) { right = t; dr = d; }
            }
            bool ch = false;
            if (left != null) ch |= ControllerCapsule(left, true);
            if (right != null) ch |= ControllerCapsule(right, false);
            if (left == null && right == null) Plugin.Log.LogInfo("VrHands: no hand models and no XR controllers found under [VR Mode] (retrying)");
            return ch;
        }

        static bool ControllerCapsule(Transform t, bool left)
        {
            var caps = left ? _vrL : _vrR;
            var fwd = t.forward;
            Add(caps, t, t.position - fwd * 0.03f, t.position + fwd * 0.09f, VrController, "XR controller", "vr");
            if (caps.Count == 0) return false;
            if (left) { LeftController = t; _vrSrcL = "XR controller"; } else { RightController = t; _vrSrcR = "XR controller"; }
            var csrc = NewSource("your " + (left ? "left" : "right") + " controller", "vr", true);
            CollectRends(csrc, t);   // the controller model under the tracked node; none found: assumed drawn
            Stamp(caps, csrc);
            if (left) { DropSource(_srcL); _srcL = csrc; } else { DropSource(_srcR); _srcR = csrc; }
            string ty = TrackerType(t);
            Plugin.Log.LogInfo("VrHands: your " + (left ? "left" : "right") + " hand from the XR controller '" + PathOf(t) + "': 1 capsule along its forward (fallback); tracked node ["
                               + (ty != null ? "tracked node (" + ty + ")" : "named controller") + "], " + csrc.Rends.Count + " renderers");
            return true;
        }

        // ---- partner rigs ----

        static bool RefreshPartners(Character her, List<Character> everyone)
        {
            bool ch = false;
            var roots = new List<Transform>();
            foreach (var go in GameRig.Partners()) if (go != null && !UnderSf(go.transform)) roots.Add(go.transform);
            if (everyone != null)
                foreach (var o in everyone)
                    if (o != null && o.Root != null && !o.Female && (her == null || o.Root != her.Root) && !roots.Contains(o.Root) && !UnderSf(o.Root)) roots.Add(o.Root);
            // rigs that are gone from the scene
            for (int i = _partners.Count - 1; i >= 0; i--)
                if (!roots.Contains(_partners[i].Root))
                {
                    DropSource(_partners[i].Src);
                    _partners.RemoveAt(i);
                    foreach (var q in _partners) q.Dirty = true;   // it may have been nested in another root, whose hands it held back
                    ch = true;
                }
            foreach (var r in roots)
            {
                if (r == null || (her != null && (r == her.Root || r.IsChildOf(her.Root))) || UnderSf(r)) continue;
                Partner p = null;
                foreach (var q in _partners) if (q.Root == r) { p = q; break; }
                if (p == null)
                {
                    p = new Partner { Root = r };
                    _partners.Add(p);
                    foreach (var q in _partners) if (q != p && p.Root.IsChildOf(q.Root)) q.Dirty = true;   // q was built holding p's hands: again, without them
                    ch = true;
                }
                else if (!p.Dirty && (p.Caps.Count > 0 || Time.unscaledTime < p.NextTry)) continue;
                bool wasDirty = p.Dirty;
                BuildPartner(p, her, roots, wasDirty);
                // an empty rig (not loaded yet, or only hands that belong to a nested rig) is looked at again later, not every 10 s:
                // each look walks the rig's whole hierarchy
                p.NextTry = Time.unscaledTime + (p.Skipped > 0 ? 60f : 30f);
                // a rebuilt rig has a new source: the capsules in Capsules still point at the old one
                if (wasDirty || p.Caps.Count != p.LoggedCount) { ch = true; p.LoggedCount = p.Caps.Count; }
            }
            return ch;
        }

        // The root nested inside `self` (another partner's root: Player/BadGuy under "[Character]") that t belongs to, else null.
        // A hand under such a root is that rig's: the rig holding the outer root must not build it a second time.
        static Transform NestedOwner(Transform t, Transform self, List<Transform> roots)
        {
            foreach (var q in roots)
                if (q != null && q != self && q.IsChildOf(self) && t.IsChildOf(q)) return q;
            return null;
        }

        // Hands and fingers of one rig: a humanoid Animator's hand/finger bones, else AdvancedSkeleton Wrist_L/Wrist_R with their
        // finger children, else any bones named like hands, wrists, palms. Hands of rigs nested inside this root are skipped
        // (the scene root "[Character]" holds Player and BadGuy: without this their hands counted twice or more).
        static void BuildPartner(Partner p, Character her, List<Transform> roots, bool wasDirty)
        {
            p.Caps.Clear();
            DropSource(p.Src); p.Src = null;
            p.Skipped = 0; p.SkippedOwners = ""; p.Dirty = false;
            var root = p.Root;
            string owner = root.name;
            var skipOwners = new List<string>();
            var handBones = new List<Transform>();   // for the renderers that draw these hands
            Animator anim = null;   // not hers, if she happens to live under the same scene root; not another rig's
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
                if (a != null && !UnderSf(a.transform) && (her == null || !a.transform.IsChildOf(her.Root)) && NestedOwner(a.transform, root, roots) == null
                    && (anim == null || (a.isHuman && !anim.isHuman))) anim = a;
            string how;
            int hands = 0, segs = 0;
            if (anim != null && anim.isHuman && anim.avatar != null)
            {
                how = "humanoid Animator";
                foreach (var left in new[] { true, false })
                {
                    int n = HumanoidHand(p.Caps, anim, left, PartnerFinger, owner, "partner");
                    var hb = anim.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                    if (hb != null) { hands++; handBones.Add(hb); }
                    segs += n;
                }
            }
            else
            {
                var wrists = new List<Transform>();
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name.StartsWith(SoftZones.Prefix) || UnderSf(t) || (her != null && t.IsChildOf(her.Root)) || IsUi(t)) continue;
                    var n = Body.Norm(t.name);
                    if (n != "wristl" && n != "wristr") continue;
                    var nested = NestedOwner(t, root, roots);
                    if (nested != null) { p.Skipped++; if (!skipOwners.Contains(nested.name)) skipOwners.Add(nested.name); continue; }
                    wrists.Add(t);
                }
                how = "AdvancedSkeleton Wrist_L/R";
                if (wrists.Count == 0 && p.Skipped == 0)
                {
                    how = "bones named like hands";
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        var n = Body.Norm(t.name);
                        if (t.childCount == 0 || t.name.StartsWith(SoftZones.Prefix) || UnderSf(t) || (her != null && t.IsChildOf(her.Root)) || IsUi(t)
                            || !(n.Contains("hand") || n.Contains("wrist") || n.Contains("palm"))) continue;
                        if (IsFingerName(t.name) || n.Contains("handle") || n.Contains("target") || n.Contains("ik") || n.Contains("twist")) continue;
                        if (NestedOwner(t, root, roots) != null) continue;
                        bool under = false;
                        foreach (var h in wrists) if (t.IsChildOf(h)) { under = true; break; }
                        if (!under) wrists.Add(t);
                    }
                }
                foreach (var w in wrists)
                {
                    segs += NonHumanoidHand(p.Caps, w, PartnerFinger, owner, "partner");
                    hands++;
                    handBones.Add(w);
                }
            }
            p.SkippedOwners = string.Join("/", skipOwners.ToArray());
            string rends = "";
            if (p.Caps.Count > 0)
            {
                var src = NewSource(owner, "partner", true);
                PartnerRends(src, root, handBones, her, roots);
                Stamp(p.Caps, src);
                p.Src = src;
                rends = ", " + src.Rends.Count + " renderers";
                if (src.Rends.Count == 0) Plugin.Log.LogInfo("VrHands: partner '" + owner + "': no renderer found: assumed drawn (the hidden-hands gate cannot apply)");
            }
            if (!wasDirty && p.Caps.Count == p.LoggedCount) return;   // a retry that found what the last build found says nothing new
            Plugin.Log.LogInfo("VrHands: partner '" + owner + "': " + how + ", " + hands + " hands" + (p.Skipped > 0 ? " (" + p.Skipped + " under " + p.SkippedOwners + " skipped)" : "")
                               + ", " + segs + " finger segments, " + p.Caps.Count + " capsules" + rends);
        }

        // The skinned meshes that draw a partner's hands: those whose bones include a hand bone or one below it (the body mesh, a
        // glove mesh); none found: every skinned mesh of the rig; still none: left empty (= assumed drawn).
        static void PartnerRends(CapSource src, Transform root, List<Transform> handBones, Character her, List<Transform> roots)
        {
            var all = new List<SkinnedMeshRenderer>();
            foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r != null && !UnderSf(r.transform) && !IsUi(r.transform) && (her == null || !r.transform.IsChildOf(her.Root)) && NestedOwner(r.transform, root, roots) == null) all.Add(r);
            foreach (var r in all)
            {
                var bones = r.bones;
                bool on = false;
                for (int i = 0; i < bones.Length && !on; i++)
                {
                    if (bones[i] == null) continue;
                    foreach (var h in handBones) if (bones[i].IsChildOf(h)) { on = true; break; }
                }
                if (on) src.Rends.Add(r);
            }
            if (src.Rends.Count == 0) foreach (var r in all) src.Rends.Add(r);
        }

        // ---- hers ----

        static bool RefreshHers(Character her)
        {
            if (her == null || her.Root == null) return false;
            if (_hersRoot == her.Root && _hers.Count > 0) return false;
            if (_hersRoot == her.Root && Time.unscaledTime < _hersNext) return false;   // her bones weren't all there yet: retry later
            _hersNext = Time.unscaledTime + 10f;
            _hersRoot = her.Root;
            int before = _hers.Count;
            _hers.Clear();
            DropSource(_hersSrc); _hersSrc = null;
            foreach (var s in new[] { "_L", "_R" })
            {
                Own(her, _hers, "Hip" + s, "Knee" + s, HerThigh, s == "_L" ? "thighL" : "thighR", 0f);
                Own(her, _hers, "Elbow" + s, "Wrist" + s, HerForearm, "forearm", 0f);
                Own(her, _hers, "Wrist" + s, "MiddleFinger1" + s, HerHand, "hand", 0.9f);
            }
            if (_hers.Count > 0) { _hersSrc = NewSource("her", "hers", false); Stamp(_hers, _hersSrc); }   // always live: her own limbs
            return _hers.Count != before || _hers.Count > 0;   // new capsules carry the new source
        }
        static float _hersNext;

        static void Own(Character c, List<SoftCapsule> into, string from, string to, float radius, string kind, float extend)
        {
            Transform a, b;
            if (!c.Bones.TryGetValue(from, out a) || !c.Bones.TryGetValue(to, out b) || a == null || b == null) return;
            Add(into, a, a.position, b.position + (b.position - a.position) * extend, radius, c.Root.name, kind);
        }

        // ---- the game's breast physics ----

        // One DynamicBoneCollider per vr/partner capsule, remembering whose it is: Attached = it is in the DynamicBones' m_Colliders
        // (a hand that isn't drawn must not push the breast physics; MirrorLive moves mirrors in and out as sources go hidden/drawn).
        sealed class Mirror { public GameObject Go; public Component Col; public CapSource Src; public bool Attached; }
        static readonly List<Mirror> _mirrors = new List<Mirror>();
        static readonly List<KeyValuePair<IList, object>> _adds = new List<KeyValuePair<IList, object>>();
        static readonly List<IList> _dbLists = new List<IList>();   // the m_Colliders lists of the chosen breast DynamicBones (read again every 10 s)
        static readonly List<Component> _dbComps = new List<Component>();   // 0.8.0: those DynamicBones themselves (Ripple's breast pulse)

        // 0.8.0: the breast DynamicBones AttachToGameBreasts chose (main chains in mode 1), refreshed with it every 10 s; empty while
        // the breast colliders are off. Root names start with Left_/Right_ (her side). DynamicBoneType: the game's DynamicBone type (null if absent).
        public static void BreastDbs(List<Component> into)
        {
            into.Clear();
            for (int i = 0; i < _dbComps.Count; i++) if (_dbComps[i] != null) into.Add(_dbComps[i]);
        }
        public static Type DynamicBoneType { get { Types(); return _db; } }
        public static Transform DynamicBoneRoot(Component db) { return db != null && _fRoot != null ? _fRoot.GetValue(db) as Transform : null; }
        static readonly List<string> _namesIn = new List<string>(), _namesOut = new List<string>();
        static int _attVersion = -1, _attMode = -1;
        static Transform _attHer;
        static float _attNext;

        // The vr and partner capsules (`caps` = Body.Contacts(): with the physics hands on that is THEIR bones, never the controller
        // models they hide) as DynamicBoneColliders in the m_Colliders of the game's breast DynamicBones under `her`. Which ones:
        // BreastColliders 1 = the main chains (root name contains "upperbreast"; none: every breast chain, said in the log),
        // 2 = every DynamicBone whose root has "breast" in its name (shaping-helper chains too: Breast_up/down/in/out, Nipple - they
        // move the areola and shape on their own), 0 = none. Safe to call every Refresh: the mirrors are rebuilt only when `caps`
        // changed (Version) or the mode did, the game's DynamicBones are looked at again every 10 s (outfit swaps bring new ones).
        public static void AttachToGameBreasts(Transform her, bool on, List<SoftCapsule> caps)
        {
            Types();
            int mode = BreastColliders < 0 ? 0 : (BreastColliders > 2 ? 2 : BreastColliders);
            if (!on || mode == 0 || her == null || _db == null || _dbc == null) { RemoveMirrors(); return; }
            bool rebuilt = _attVersion != Version || _attHer != her || _attMode != mode;
            if (!rebuilt && Time.unscaledTime < _attNext) return;
            _attNext = Time.unscaledTime + 10f;
            if (rebuilt)
            {
                RemoveMirrors();
                _attVersion = Version;
                _attHer = her;
                _attMode = mode;
                foreach (var c in caps ?? Capsules)
                {
                    if (c.T == null || (c.Kind != "vr" && c.Kind != "partner")) continue;
                    var col = MakeCollider(c);
                    if (col == null) continue;
                    col.Src = c.Src;
                    col.Attached = c.Src == null || !c.Src.Gated || c.Src.Live;
                    var beh = col.Col as Behaviour;
                    if (beh != null) beh.enabled = col.Attached;
                    _mirrors.Add(col);
                }
            }
            var objs = UnityEngine.Object.FindObjectsOfType(_db);
            bool anyMain = false;
            if (mode == 1)
                foreach (var o in objs)
                {
                    var root = _fRoot.GetValue(o) as Transform;
                    if (root != null && root.IsChildOf(her) && Body.Norm(root.name).Contains("upperbreast")) { anyMain = true; break; }
                }
            _dbLists.Clear(); _dbComps.Clear(); _namesIn.Clear(); _namesOut.Clear();
            int added = 0;
            foreach (var o in objs)
            {
                var db = o as Component;
                if (db == null) continue;
                var root = _fRoot.GetValue(db) as Transform;
                if (root == null || !root.IsChildOf(her)) continue;
                var nm = Body.Norm(root.name);
                if (!nm.Contains("breast")) continue;
                if (mode == 1 && anyMain && !nm.Contains("upperbreast")) { _namesOut.Add(root.name); continue; }
                _namesIn.Add(root.name);
                var list = _fColliders.GetValue(db) as IList;
                if (list == null) { list = (IList)Activator.CreateInstance(_fColliders.FieldType); _fColliders.SetValue(db, list); }
                _dbLists.Add(list);
                _dbComps.Add(db);
                foreach (var m in _mirrors)
                    if (m.Attached && m.Col != null && !list.Contains(m.Col)) { list.Add(m.Col); _adds.Add(new KeyValuePair<IList, object>(list, m.Col)); added++; }
            }
            if (rebuilt || added > 0)
            {
                int detached = 0;
                foreach (var m in _mirrors) if (!m.Attached) detached++;
                Plugin.Log.LogInfo("VrHands: " + _mirrors.Count + " hand colliders" + (detached > 0 ? " (" + detached + " of hidden hands detached)" : "") + " on " + _namesIn.Count
                                   + " breast DynamicBones [" + NameList(_namesIn) + "]" + (_namesOut.Count > 0 ? "; skipped " + _namesOut.Count + " [" + NameList(_namesOut) + "]" : "")
                                   + (mode == 1 && !anyMain && _namesIn.Count > 0 ? "; no 'UpperBreast' chain found: fell back to every breast chain" : "")
                                   + " (" + added + " added, mode " + mode + ")");
            }
        }

        static string NameList(List<string> names)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < names.Count && i < 8; i++) { if (i > 0) sb.Append(", "); sb.Append(names[i]); }
            if (names.Count > 8) sb.Append(", ... +").Append(names.Count - 8);
            return sb.ToString();
        }

        // A DynamicBoneCollider capsule between the capsule's world endpoints, on a child of its bone with world scale ~1 (sizes in metres).
        static Mirror MakeCollider(SoftCapsule c)
        {
            Vector3 a = c.T.TransformPoint(c.LocalA), b = c.T.TransformPoint(c.LocalB);
            var dir = b - a; float len = dir.magnitude;
            if (len < 1e-5f) return null;
            var go = new GameObject(SoftZones.Prefix + "_vrcol_" + c.Kind + "_" + c.T.name);
            go.transform.SetParent(c.T, false);
            go.transform.position = (a + b) * 0.5f;
            go.transform.rotation = Quaternion.FromToRotation(Vector3.up, dir / len);
            float s = Mathf.Abs(c.T.lossyScale.x); if (s < 1e-6f) s = 1f;
            go.transform.localScale = Vector3.one / s;
            var col = go.AddComponent(_dbc);
            Set(col, "m_Radius", c.Radius);
            Set(col, "m_Height", len + 2f * c.Radius);
            Set(col, "m_Direction", 1);   // Y
            Set(col, "m_Center", Vector3.zero);
            return new Mirror { Go = go, Col = col };
        }

        static void RemoveMirrors()
        {
            foreach (var kv in _adds)
            {
                try { if (kv.Key != null) kv.Key.Remove(kv.Value); } catch (Exception) { }
            }
            _adds.Clear();
            _dbLists.Clear(); _dbComps.Clear();
            foreach (var m in _mirrors) if (m.Go != null) UnityEngine.Object.Destroy(m.Go);
            _mirrors.Clear();
            _attVersion = -1;
            _attMode = -1;
            _attHer = null;
        }

        public static void Release()
        {
            RemoveMirrors();
            // our sources only: the physics hands drop theirs
            DropSource(_srcL); DropSource(_srcR); DropSource(_hersSrc);
            foreach (var p in _partners) DropSource(p.Src);
            _srcL = _srcR = _hersSrc = null;
            _vrL.Clear(); _vrR.Clear(); _partners.Clear(); _hers.Clear(); _hersRoot = null;
            _vrSrcL = _vrSrcR = "";
            _vrNext = _hersNext = 0f;
            Capsules.Clear();
            Version++;
            LeftController = RightController = null;
            Status = "";
            _statusDirty = false;
            _camNext = 0f;
        }

        // ---- survey: one log block per session ----

        const int SurveyLines = 400, SurveyDepth = 12, SurveySiblings = 20;
        static int _lines, _lineCap;
        static bool _cut;

        public static void Survey(Character her)
        {
            if (_surveyed) return;
            _surveyed = true;
            Types();
            _lines = 0; _cut = false; _lineCap = SurveyLines;
            var sb = new StringBuilder("VR rig survey:");
            try
            {
                Fields(sb, "VR_HandController", _hand);
                Fields(sb, "PlayerController", _player, "vrCameraType", "vrPovActive", "vr_PovCamera_Location", "camera_vrCam", "vr_Camera_Mov");
                if (_camSys != null)
                {
                    var cs = UnityEngine.Object.FindObjectOfType(_camSys);
                    Line(sb, "Camera_system " + (cs == null ? "not found" : "camera_VRCam = " + ValueStr(Get(cs, "camera_VRCam"))));
                }
                Cameras(sb);
                // hierarchies share what is left of the line budget
                var trees = new List<KeyValuePair<string, Transform>>();
                if (_process != null)
                {
                    var vr = Get(UnityEngine.Object.FindObjectOfType(_process), "VR_Mode") as GameObject;
                    if (vr != null) trees.Add(new KeyValuePair<string, Transform>("[VR Mode] (VR_GameProcess_System.VR_Mode)", vr.transform));
                    else Line(sb, "VR_GameProcess_System.VR_Mode: " + (_process != null ? "null / type not found" : "no type"));
                }
                foreach (var go in GameRig.Partners()) if (go != null) trees.Add(new KeyValuePair<string, Transform>(go.name + " (MovePlayerAndGirl partner)", go.transform));
                for (int i = 0; i < trees.Count; i++)
                {
                    _cut = false;
                    _lineCap = _lines + Mathf.Max(20, (SurveyLines - _lines) / (trees.Count - i));
                    Line(sb, "hierarchy " + trees[i].Key + ":");
                    Dump(sb, trees[i].Value, 0);
                }
            }
            catch (Exception e) { sb.Append("\n  survey stopped: ").Append(e.GetType().Name).Append(' ').Append(e.Message); }
            Plugin.Log.LogInfo(sb.ToString());
        }

        static void Line(StringBuilder sb, string s)
        {
            if (s.Length == 0) return;
            _lines++;
            sb.Append("\n  ").Append(s);
        }

        static string PathOf(object o)
        {
            Transform t = o as Transform;
            var go = o as GameObject; if (go != null) t = go.transform;
            var c = o as Component; if (c != null) t = c.transform;
            if (t == null) return o == null ? "null" : o.ToString();
            var parts = new List<string>();
            for (int i = 0; t != null && i < 16; i++, t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        static string ValueStr(object v)
        {
            if (v == null) return "null";
            var uo = v as UnityEngine.Object;
            if (!ReferenceEquals(uo, null) && uo == null) return "destroyed";
            if (v is Transform || v is GameObject || v is Component)
            {
                var s = PathOf(v) + " <" + v.GetType().Name + ">";
                var anim = v as Animator;
                if (anim != null) s += " isHuman=" + anim.isHuman + " enabled=" + anim.enabled;
                var beh = v as Behaviour;
                if (beh != null && anim == null) s += " enabled=" + beh.enabled;
                return s;
            }
            var arr = v as Array;
            if (arr != null) return v.GetType().GetElementType().Name + "[" + arr.Length + "]";
            return v.ToString();
        }

        // The named fields of the first instance of a game type (all declared instance fields when none are named).
        static void Fields(StringBuilder sb, string label, Type type, params string[] names)
        {
            if (type == null) { Line(sb, label + ": type not found"); return; }
            var o = UnityEngine.Object.FindObjectOfType(type);
            if (o == null) { Line(sb, label + ": no instance in the scene"); return; }
            var c = o as Component;
            Line(sb, label + " on " + PathOf(o) + (c != null && c is Behaviour ? " enabled=" + ((Behaviour)c).enabled : ""));
            if (names.Length > 0) { foreach (var n in names) Line(sb, "  " + n + " = " + ValueStr(Get(o, n))); return; }
            int k = 0;
            foreach (var f in type.GetFields(Any))
            {
                if (f.DeclaringType.Assembly.GetName().Name.StartsWith("UnityEngine") || f.IsStatic || ++k > 40) continue;
                object v = null;
                try { v = f.GetValue(o); } catch (Exception) { }
                Line(sb, "  " + f.Name + ":" + f.FieldType.Name + " = " + ValueStr(v));
            }
        }

        static void Cameras(StringBuilder sb)
        {
            int n = 0;
            foreach (var o in Resources.FindObjectsOfTypeAll(typeof(Camera)))
            {
                var cam = o as Camera;
                if (cam == null || !cam.gameObject.scene.IsValid()) continue;
                if (++n > 20) continue;
                Line(sb, "Camera '" + cam.name + "' enabled=" + cam.enabled + " active=" + cam.gameObject.activeInHierarchy + " stereoTargetEye=" + cam.stereoTargetEye
                         + " depth=" + cam.depth + " targetTexture=" + (cam.targetTexture != null) + " path " + PathOf(cam.transform));
            }
            Line(sb, n + " cameras" + (n > 20 ? " (first 20 listed)" : ""));
        }

        // Components worth knowing about at a node: type names, SkinnedMeshRenderer mesh + bone count, RootMotion.* enabled + IK weight.
        static string Describe(Component c)
        {
            if (c == null) return "<missing script>";
            var t = c.GetType();
            try { return DescribeInner(c, t); } catch (Exception) { return t.Name; }
        }

        static string DescribeInner(Component c, Type t)
        {
            var sb = new StringBuilder(t.Name);
            var smr = c as SkinnedMeshRenderer;
            if (smr != null) sb.Append("(mesh ").Append(smr.sharedMesh != null ? smr.sharedMesh.name : "-").Append(", ").Append(smr.bones != null ? smr.bones.Length : 0).Append(" bones)");
            var anim = c as Animator;
            if (anim != null) sb.Append("(isHuman ").Append(anim.isHuman).Append(anim.enabled ? "" : ", off").Append(')');
            else if (t.Namespace != null && t.Namespace.StartsWith("RootMotion"))
            {
                var beh = c as Behaviour;
                sb.Append("(RootMotion enabled=").Append(beh != null ? beh.enabled.ToString() : "?");
                var solver = Get(c, "solver");
                var w = Get(solver, "IKPositionWeight");
                if (w != null) sb.Append(" IKPositionWeight=").Append(w);
                if (t.Name == "VRIK")
                    sb.Append(" head=").Append(TargetOf(Get(Get(solver, "spine"), "headTarget")))
                      .Append(" lArm=").Append(TargetOf(Get(Get(solver, "leftArm"), "target")))
                      .Append(" rArm=").Append(TargetOf(Get(Get(solver, "rightArm"), "target")));
                else if (t.Name == "FullBodyBipedIK")
                    sb.Append(" lHand=").Append(TargetOf(Get(Get(solver, "leftHandEffector"), "target")))
                      .Append(" rHand=").Append(TargetOf(Get(Get(solver, "rightHandEffector"), "target")));
                sb.Append(')');
            }
            else
            {
                var beh = c as Behaviour;
                if (beh != null && !beh.enabled) sb.Append("(off)");
            }
            return sb.ToString();
        }

        static string TargetOf(object t) { return t == null || (t is UnityEngine.Object && (UnityEngine.Object)t == null) ? "-" : PathOf(t); }

        static readonly List<Component> _comps = new List<Component>();

        // One line per node; a run of plain single-child bones ("a > b > c") is one line; > 20 siblings are collapsed.
        static void Dump(StringBuilder sb, Transform t, int depth)
        {
            if (_lines >= _lineCap) { if (!_cut) { _cut = true; Line(sb, "... (line budget reached)"); } return; }
            var names = new StringBuilder(t.name);
            var node = t;
            for (int guard = 0; guard < SurveyDepth; guard++)
            {
                node.GetComponents(_comps);
                if (_comps.Count != 1 || node.childCount != 1) break;   // only the Transform, and exactly one child: merge with it
                node = node.GetChild(0);
                names.Append(" > ").Append(node.name);
            }
            node.GetComponents(_comps);
            var line = new StringBuilder(new string(' ', Mathf.Min(depth, SurveyDepth) * 2));
            line.Append(names);
            var types = new List<string>();
            foreach (var c in _comps) if (!(c is Transform) || c is RectTransform) types.Add(Describe(c));
            if (types.Count > 0) line.Append(" [").Append(string.Join(", ", types.ToArray())).Append(']');
            Line(sb, line.ToString());
            if (depth + 1 > SurveyDepth) { if (node.childCount > 0) Line(sb, new string(' ', SurveyDepth * 2) + "... (depth limit)"); return; }
            int n = node.childCount;
            int shown = n > SurveySiblings ? 5 : n;
            for (int i = 0; i < shown; i++) Dump(sb, node.GetChild(i), depth + 1);
            if (shown < n) Line(sb, new string(' ', (depth + 1) * 2) + "... +" + (n - shown) + " more siblings collapsed (" + node.GetChild(shown).name + ", " + node.GetChild(n - 1).name + ")");
        }
    }
}
