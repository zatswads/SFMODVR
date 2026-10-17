using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SfMorph
{
    // Desktop-window panel (F8): pick the woman, then Body (VaM-style sliders by
    // body area), Bones (any skin bone), Presets and Characters (converted VaM characters), Physics (what her
    // body does: soft zones, secondary motion, pushes, wrinkles) VR (Embody, your hands, who touches her) and Reactions (how she reacts to touch).
    internal partial class Panel
    {
        readonly Plugin _p;
        readonly Body _body;
        public bool Visible;
        Rect _rect = new Rect(700, 40, 560, 720);
        Vector2 _scroll;
        Character _sel;
        string _boneFilter = "", _presetName = "";
        readonly HashSet<string> _open = new HashSet<string> { "Breasts" };
        bool _changedOnly, _showBones;
        string _bone;
        string _message = "", _exportMsg = "";
        List<string> _presets;
        GUIStyle _hint, _bold, _off;
        const int MaxRows = 80; // IMGUI gets slow with hundreds of sliders; the filter narrows it

        static readonly string[] Tabs = { "Body", "Bones", "Presets", "Characters", "Physics", "VR", "Reactions", "Skin", "Debug" };   // appended: the saved Tab indices stay valid

        public Panel(Plugin p, Body body)
        {
            _p = p;
            _body = body;
            var f = p.PanelRect.Value.Split(',');
            float x, y, w, h;
            if (f.Length == 4 && float.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) && float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                && float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out w) && float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out h))
                _rect = new Rect(x, y, Mathf.Max(400, w), Mathf.Max(300, h));
        }

        public void Draw()
        {
            if (!Visible) return;
            if (_hint == null)
            {
                _hint = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
                _hint.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
                _bold = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
                _off = new GUIStyle(_hint);
                _off.normal.textColor = new Color(1f, 0.75f, 0.4f);
                _fold = new GUIStyle(_hint) { wordWrap = false };
            }
            float scale = Mathf.Clamp(_p.UiScale.Value, 0.75f, 2.5f);
            var old = GUI.matrix;
            var before = _rect;
            Resize(scale);   // before the window: a press on its border is taken here, never by the window's own controls
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            // 0.11.7: GUI.Window, not GUILayout.Window. The layout window grew and shrank with its content (a status line wrapping to one
            // more line, a greyed reason showing up), so the panel jumped whenever an option changed or a debug line updated, and the size
            // given to it was only a minimum (no way to make it smaller). This one keeps exactly its size; the content scrolls inside.
            _rect = GUI.Window(0x5F4D4F, _rect, Window, "SF Body Morphs " + Plugin.Version);
            float sw = Screen.width / scale, sh = Screen.height / scale;
            _rect.width = Mathf.Clamp(_rect.width, MinW, Mathf.Max(MinW, sw));
            _rect.height = Mathf.Clamp(_rect.height, MinH, Mathf.Max(MinH, sh));
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, sw - _rect.width));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, sh - _rect.height));
            if (_rect != before && _resizing == 0) SaveRect();
            GUI.matrix = old;
        }

        const float MinW = 460f, MinH = 300f, Edge = 7f;
        int _resizing;            // sides being dragged: 1 left, 2 right, 4 bottom, 8 top
        Vector2 _resizeFrom;
        Rect _resizeRect;
        GUIStyle _fold;

        void SaveRect()
        {
            _p.PanelRect.Value = string.Format(CultureInfo.InvariantCulture, "{0:0},{1:0},{2:0},{3:0}", _rect.x, _rect.y, _rect.width, _rect.height);
        }

        // Any edge or corner resizes (like the SF Neural Rendering panel); the title bar moves it. The mouse is in unscaled screen GUI points
        // here (GUI.matrix not set yet), so it is divided by the panel scale. Drags are followed outside the window too (rawType).
        void Resize(float scale)
        {
            var e = Event.current;
            if (e == null) return;
            var m = e.mousePosition / scale;
            if (_resizing != 0)
            {
                if (e.rawType == EventType.MouseUp) { _resizing = 0; SaveRect(); e.Use(); return; }
                if (e.rawType == EventType.MouseDrag)
                {
                    var d = m - _resizeFrom;
                    var r = _resizeRect;
                    if ((_resizing & 2) != 0) r.width = Mathf.Max(MinW, _resizeRect.width + d.x);
                    if ((_resizing & 4) != 0) r.height = Mathf.Max(MinH, _resizeRect.height + d.y);
                    if ((_resizing & 1) != 0) { float w = Mathf.Max(MinW, _resizeRect.width - d.x); r.x = _resizeRect.xMax - w; r.width = w; }
                    if ((_resizing & 8) != 0) { float h = Mathf.Max(MinH, _resizeRect.height - d.y); r.y = _resizeRect.yMax - h; r.height = h; }
                    _rect = r;
                    e.Use();
                }
                return;
            }
            if (e.type != EventType.MouseDown || e.button != 0) return;
            var outer = new Rect(_rect.x - Edge, _rect.y - Edge, _rect.width + 2f * Edge, _rect.height + 2f * Edge);
            if (!outer.Contains(m)) return;
            int mask = 0;
            if (m.x <= _rect.x + Edge) mask |= 1;
            if (m.x >= _rect.xMax - Edge) mask |= 2;
            if (m.y >= _rect.yMax - Edge) mask |= 4;
            if (m.y <= _rect.y + 3f) mask |= 8;   // only the outermost pixels of the top: the title bar below moves the window
            if (mask == 0) return;
            _resizing = mask; _resizeFrom = m; _resizeRect = _rect;
            e.Use();
        }

        // Tabs in screen order; the numbers are the saved [Interface] Tab values (an appended tab keeps the old values valid).
        static readonly int[] TabOrder = { 0, 1, 2, 3, 7, 4, 5, 6, 8 };

        void Window(int id)
        {
            GUILayout.BeginHorizontal();
            bool on = GUILayout.Toggle(_p.Apply.Value, " Apply changes", GUILayout.Width(150));
            if (on != _p.Apply.Value) _p.Apply.Value = on;
            GUILayout.Label(_p.Apply.Value ? "" : "off: showing the game's own look", _off);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close (" + _p.PanelKey.Value + ")", GUILayout.Width(110))) Visible = false;
            GUILayout.EndHorizontal();

            // Women first; everyone (except your own body) only if nobody could be identified as female.
            var people = new List<Character>();
            foreach (var c in _body.Found) if (c.Female && !c.IsPlayer) people.Add(c);
            if (people.Count == 0) foreach (var c in _body.Found) if (!c.IsPlayer && c.BoneList.Count > 0) people.Add(c);
            GUILayout.Label(_body.Status, _hint);
            if (_sel != null && !people.Contains(_sel)) _sel = null;
            if (_sel == null && people.Count > 0) _sel = people[0];
            GUILayout.BeginHorizontal();
            Character pick = null;
            foreach (var c in people)
                if (GUILayout.Toggle(_sel == c, c.Key + (c.Look.Empty ? "" : " *"), GUI.skin.button) && _sel != c) pick = c;
            if (_sel != null)
            {
                GUI.enabled = !_sel.Look.Empty;
                if (GUILayout.Button("Reset " + _sel.Key, GUILayout.Width(130))) { _sel.Look.Clear(); _body.Touch(_sel); }
                GUI.enabled = true;
            }
            GUILayout.EndHorizontal();
            if (pick != null) _sel = pick;
            if (_sel == null)
            {
                GUILayout.Label("Load a scene with a woman in it. Characters are rescanned every 2 seconds.", _hint);
                GUI.DragWindow(new Rect(0, 0, 10000, 20));
                return;
            }

            // 0.11.7: the tabs in a column on the left (eight no longer fit in one row)
            int tab = Mathf.Clamp(_p.Tab.Value, 0, Tabs.Length - 1);
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(96));
            int pickTab = -1;
            foreach (int t in TabOrder)
                if (GUILayout.Toggle(tab == t, Tabs[t], GUI.skin.button) && tab != t) pickTab = t;
            GUILayout.EndVertical();
            _scroll = GUILayout.BeginScrollView(_scroll);
            switch (tab)
            {
                case 0: BodyTab(_sel); break;
                case 1: BoneTab(_sel); break;
                case 2: PresetTab(_sel); break;
                case 3: CharacterTab(_sel); break;
                case 4: PhysicsTab(_sel); break;
                case 5: VrTab(_sel); break;
                case 7: SkinTab(_sel); break;
                case 8: DebugTab(_sel); break;
                default: ReactionsTab(_sel); break;
            }
            GUILayout.EndScrollView();
            GUILayout.EndHorizontal();
            if (pickTab >= 0) { _p.Tab.Value = pickTab; _scroll = Vector2.zero; }   // after the pass: the same controls in the Layout and click passes
            GUI.DragWindow(new Rect(0, 0, 10000, 20));   // the title bar moves it; the edges resize it (Resize)
        }

        // ---- tabs ----

        void BodyTab(Character c)
        {
            GUILayout.Label("On top of the game's own shape. Sizes scale, moves are in cm. Each slider shows the bones it uses; " +
                            "one that can't apply is greyed out with the reason. More: the Bones tab.", _hint);
            foreach (var g in Detail.Groups)
            {
                int changed = 0;
                foreach (var d in Detail.All) if (d.Group == g && c.Look.Regions.ContainsKey(d.Id)) changed++;
                bool open = _open.Contains(g);
                if (GUILayout.Button((open ? "- " : "+ ") + g + (changed > 0 ? "  (" + changed + " changed)" : ""), _bold))
                    if (open) _open.Remove(g); else _open.Add(g);
                if (!open) continue;
                foreach (var d in Detail.All)
                {
                    if (d.Group != g) continue;
                    var bones = c.DetailBones[d.Id];
                    float v;
                    if (!c.Look.Regions.TryGetValue(d.Id, out v)) v = d.Neutral;
                    string off;
                    c.Off.TryGetValue(d.Id, out off);
                    float nv = Slider(d.Label, v, d.Min, d.Max, d.Op == Op.Move ? "+0.0;-0.0;0" : "0.00", d.Neutral, off == null ? null : "off: " + off, d.Unit);
                    if (off == null && d.Op != Op.Blend)   // 0.11.23: a Blend slider (Athletic upper body) adds to this one
                    {
                        float ev = Body.Effective(c.Look, d);
                        if (Mathf.Abs(ev - v) > 0.004f) GUILayout.Label("      with the build sliders: " + ev.ToString(d.Op == Op.Move ? "+0.0;-0.0;0" : "0.00") + d.Unit, _hint);
                    }
                    if (off == null && _showBones)
                    {
                        var names = "";
                        foreach (var b in bones) names += (names.Length > 0 ? ", " : "") + b.name;
                        string src;
                        c.Source.TryGetValue(d.Id, out src);
                        GUILayout.Label("      " + names + "  (" + src + ")", _hint);
                    }
                    if (nv != v)
                    {
                        if (Mathf.Abs(nv - d.Neutral) < 0.005f) c.Look.Regions.Remove(d.Id); else c.Look.Regions[d.Id] = nv;
                        _body.Touch(c);
                    }
                }
            }
            _showBones = GUILayout.Toggle(_showBones, " Show which bones each slider uses");
            GUILayout.Label("Big changes to hips, legs or arms move where bodies meet in the game's animations; head size nudges its eyelid tracking.", _hint);
        }

        void BoneTab(Character c)
        {
            GUILayout.Label("Scale any bone of the skin (its children scale with it). Unequal X/Y/Z on a bone with children " +
                            "skews them when they rotate; the Body tab's sliders avoid that.", _hint);
            Transform sel;
            if (_bone != null && c.Bones.TryGetValue(_bone, out sel))
            {
                Vector3 v;
                if (!c.Look.Bones.TryGetValue(_bone, out v)) v = Vector3.one;
                GUILayout.Label(_bone, _bold);
                float u = Slider("All", (v.x + v.y + v.z) / 3f, 0.3f, 3f, "0.00", 1f);
                var nv = Mathf.Abs(u - (v.x + v.y + v.z) / 3f) > 0.0001f ? new Vector3(u, u, u) : v;
                nv.x = Slider("X", nv.x, 0.3f, 3f, "0.00", 1f);
                nv.y = Slider("Y", nv.y, 0.3f, 3f, "0.00", 1f);
                nv.z = Slider("Z", nv.z, 0.3f, 3f, "0.00", 1f);
                GUILayout.Label("X/Y/Z are the bone's own axes, which differ from bone to bone.", _hint);
                if (nv != v)
                {
                    if ((nv - Vector3.one).sqrMagnitude < 0.0001f) c.Look.Bones.Remove(_bone); else c.Look.Bones[_bone] = nv;
                    _body.Touch(c);
                }
                GUILayout.Space(6);
            }
            Filter(ref _boneFilter, c.Look.Bones.Count);
            int matched = 0;
            string pickBone = null;
            foreach (var t in c.BoneList)
            {
                if (t == null) continue;
                bool set = c.Look.Bones.ContainsKey(t.name);
                if (_changedOnly && !set) continue;
                if (_boneFilter.Length > 0 && t.name.IndexOf(_boneFilter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (++matched > MaxRows) continue;
                if (GUILayout.Toggle(_bone == t.name, (set ? "* " : "") + t.name, GUI.skin.button) && _bone != t.name) pickBone = t.name;
            }
            if (pickBone != null) _bone = pickBone;
            if (matched > MaxRows) GUILayout.Label((matched - MaxRows) + " more: type in the filter to narrow the list.", _hint);
        }

        void PresetTab(Character c)
        {
            GUILayout.Label("Each woman's changes save by themselves and come back when she appears again. Presets are " +
                            "looks saved by name, to put on anyone. Files: " + Store.Dir, _hint);
            GUILayout.BeginHorizontal();
            _presetName = GUILayout.TextField(_presetName, GUILayout.MinWidth(200));
            GUI.enabled = _presetName.Trim().Length > 0 && !c.Look.Empty;
            if (GUILayout.Button("Save " + c.Key + "'s look", GUILayout.Width(180)))
            {
                Store.SavePreset(_presetName.Trim(), c.Look);
                _message = "Saved preset '" + _presetName.Trim() + "'.";
                _presets = null;
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (c.Look.Empty) GUILayout.Label("off: nothing changed on " + c.Key + " yet", _off);
            if (_message.Length > 0) GUILayout.Label(_message, _hint);
            GUILayout.Space(8);
            GUILayout.Label("Character pipeline", _bold);
            GUILayout.Label("Writes her meshes (game vertex order, UVs, bone weights, bind-pose skeleton) for turning a VaM character " +
                            "into a morph for her. Files go to " + System.IO.Path.Combine(Store.Dir, "export") + ".", _hint);
            if (GUILayout.Button("Export " + c.Key + " for the character pipeline")) _exportMsg = Export.Run(c);
            if (_exportMsg.Length > 0) GUILayout.Label(_exportMsg, _hint);
            GUILayout.Space(8);
            if (_presets == null) _presets = Store.Presets();
            if (_presets.Count == 0) GUILayout.Label("No presets yet.", _hint);
            foreach (var name in _presets)
                if (GUILayout.Button("Load '" + name + "' onto " + c.Key))
                {
                    var look = new Look();
                    if (Store.LoadPreset(name, look)) { c.Look.CopyFrom(look); _body.Touch(c); _message = "Loaded '" + name + "' onto " + c.Key + "."; }
                }
        }

        void CharacterTab(Character c)
        {
            GUILayout.Label("VaM characters converted for her (tools/pipeline/run.ps1): her own meshes, rig, clothes and animations with " +
                            "their shape and skin. The shape is a real blendshape, so the Body and Bones sliders still work on top. Folder: " + MorphSet.Root, _hint);
            var all = MorphSet.All;
            bool had = c.Look.Morph != null;  // IMGUI: the same controls in the Layout pass and the click pass
            const int cols = 3;
            const float card = 150f;
            int i = 0;
            string pick = null; bool pickNone = false;
            GUILayout.BeginHorizontal();
            if (Card(null, "None", c.Look.Morph == null, card)) pickNone = true;
            i++;
            foreach (var m in all)
            {
                if (i % cols == 0) { GUILayout.FlexibleSpace(); GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
                if (Card(m.Thumb, m.Name, c.Look.Morph == m.Name, card)) pick = m.Name;
                i++;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (pickNone && c.Look.Morph != null) { c.Look.Morph = null; _body.Touch(c); }
            if (pick != null && c.Look.Morph != pick)
            {
                var ps = MorphSet.Find(pick);
                bool fo = ps != null && ps.FaceOnly;   // a face-only character starts as Face only (body and its skin stay hers)
                c.Look.Morph = pick; c.Look.MorphFace = 1f; c.Look.MorphBody = fo ? 0f : 1f; c.Look.MorphSkin = true; c.Look.MorphSkinBody = !fo;
                c.Look.ResetMorphExtras(); _body.Touch(c);
            }
            if (all.Count == 0)
                GUILayout.Label("off: no converted characters yet. Export her (Presets tab) and a VaM character (SfExport plugin), then run tools/pipeline/run.ps1.", _off);
            if (GUILayout.Button("Rescan the folder", GUILayout.Width(160))) MorphSet.Rescan();

            AvatarSection(c);

            // Pose check: records the game's own animation of her for the pipeline (PoseRecorder).
            string recStatus = PoseRecorder.Status;   // read once: the same controls in the Layout pass and the click pass
            bool recBusy = PoseRecorder.Busy;
            GUILayout.Space(6);
            GUI.enabled = !recBusy;
            if (GUILayout.Button("Record 6 s of her animation (pose check)")) PoseRecorder.Start(c, 6f, 10f, _p);
            GUI.enabled = true;
            GUILayout.Label("For the pipeline's pose check: turn Apply changes, soft zones, Embody and physics hands off first to record the game's own pose.", _hint);
            if (recStatus.Length > 0) GUILayout.Label(recStatus, recStatus.StartsWith("off") ? _off : _hint);

            if (had && c.Look.Morph != null) MorphControls(c);  // a pick shows its controls from the next frame on
        }

        // ---- Physics (her body: soft zones, motion, pushes, wrinkles) and VR (Embody, your hands, who touches her) ----

        static readonly string[] HandModes = { "None", "Left", "Right", "Both" };
        static readonly string[] ViewModes = { "Game camera", "Snap to his eyes", "Follow his eyes" };
        static readonly string[] Partners = { "Player", "Other man" };

        static readonly string[] HandMeshes = { "Game hands", "Simple" };
        static readonly string[] BodyColliders = { "Convex hulls", "Capsules" };

        void PhysicsTab(Character c)
        {
            PhysicsSection(c);
            GUILayout.Space(10);
            MotionSection(c);
            GUILayout.Space(10);
            CorrectivesSection(c);
            GUILayout.Space(10);
            PushesSection(c);
        }

        // 0.11.47 pose correctives ([Correctives], PoseDriver.cs): her clavicles / shoulder blades follow her arms over the game's animations
        void CorrectivesSection(Character c)
        {
            GUILayout.Label("Pose correctives", _bold);
            string off = !_p.Apply.Value ? "'Apply changes' is off" : !c.Female || c.IsPlayer ? "only for the game's woman" : null;
            GUI.enabled = off == null;
            bool on = GUILayout.Toggle(PoseDriver.On.Value, " Shoulders follow her arms (clavicle and shoulder blade rise, come forward or go back with the arm)");
            GUI.enabled = true;
            if (off == null && on != PoseDriver.On.Value) PoseDriver.On.Value = on;
            string sub = off ?? (PoseDriver.On.Value ? null : "pose correctives are off");
            float a = Slider("  Clavicle amount", PoseDriver.ClavicleAmount.Value * 100f, 0f, 100f, "0", 60f, sub == null ? null : "off: " + sub, " %") / 100f;
            if (sub == null && Mathf.Abs(a - PoseDriver.ClavicleAmount.Value) > 0.0001f) PoseDriver.ClavicleAmount.Value = a;
            GUI.enabled = off == null;
            GUILayout.BeginHorizontal();
            bool dbg = GUILayout.Toggle(PoseDriver.Debug.Value, " Log every 10 s");
            if (off == null && dbg != PoseDriver.Debug.Value) PoseDriver.Debug.Value = dbg;
            if (GUILayout.Button("Log pose now", GUILayout.Width(110f))) PoseDriver.LogNow();
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            if (off != null) GUILayout.Label("off: " + off, _off);
            else if (PoseDriver.Status.Length > 0) GUILayout.Label(PoseDriver.Status, PoseDriver.Status.StartsWith("off") ? _off : _hint);
            GUILayout.Label("The game's animations hardly move her shoulders: a raised arm turns under a shoulder that stays down. This adds the natural " +
                            "shoulder-girdle motion (about a third of the arm's lift beyond 30 deg, forward when reaching forward or across, back when reaching back); " +
                            "the arm still points where the animation puts it. 'Log pose now' writes both shoulders' angles and driver weights to the log (for poses that look wrong).", _hint);
        }

        // 0.11.7 (user: wrinkles and muscles are not physics): how her skin looks; the dents stay with the contacts in Physics
        void SkinTab(Character c)
        {
            BuildSection(c);
            GUILayout.Space(10);
            WrinklesSection(c);
            GUILayout.Space(10);
            MusclesSection(c);
            GUILayout.Space(10);
            ChubbySection(c);
            GUILayout.Space(10);
            MarksSection(c);
        }

        void VrTab(Character c)
        {
            EmbodySection(c);
            GUILayout.Space(10);
            HandsSection(c);
            GUILayout.Space(10);
            ContactsSection(c);
            GUILayout.Space(10);
            HapticsSection(c);
        }

        // 0.11.26 controller haptics ([Haptics]); the per-actuator tests are in the Debug tab
        void HapticsSection(Character c)
        {
            GUILayout.Label("Haptics", _bold);
            bool onWas = _p.HapticsOn.Value;   // read before the controls: the same rows in the Layout pass and the click pass
            bool on = GUILayout.Toggle(onWas, " Controllers vibrate with what your hands touch on her");
            if (on != _p.HapticsOn.Value) _p.HapticsOn.Value = on;
            string off = !onWas ? "haptics are off" : !(_p.Apply.Value && _p.Physics.Value) && !_p.HandsOn.Value ? "her body is not built (soft zones or physics hands)" : null;
            string offS = !onWas ? "off: haptics are off" : null;
            float v = Slider("Strength", _p.HapticsStrength.Value * 100f, 0f, 100f, "0", 100f, offS, " %") / 100f;
            if (offS == null && Mathf.Abs(v - _p.HapticsStrength.Value) > 0.001f) _p.HapticsStrength.Value = v;
            v = Slider("Touch and rub", _p.HapticsTouch.Value * 100f, 0f, 100f, "0", 50f, offS, " %") / 100f;
            if (offS == null && Mathf.Abs(v - _p.HapticsTouch.Value) > 0.001f) _p.HapticsTouch.Value = v;
            v = Slider("Pats and slaps", _p.HapticsImpact.Value * 100f, 0f, 100f, "0", 100f, offS, " %") / 100f;
            if (offS == null && Mathf.Abs(v - _p.HapticsImpact.Value) > 0.001f) _p.HapticsImpact.Value = v;
            v = Slider("Grab", _p.HapticsGrab.Value * 100f, 0f, 100f, "0", 60f, offS, " %") / 100f;
            if (offS == null && Mathf.Abs(v - _p.HapticsGrab.Value) > 0.001f) _p.HapticsGrab.Value = v;
            v = Slider("Penetration", _p.HapticsPen.Value * 100f, 0f, 100f, "0", 70f, offS, " %") / 100f;
            if (offS == null && Mathf.Abs(v - _p.HapticsPen.Value) > 0.001f) _p.HapticsPen.Value = v;
            if (off != null && onWas) GUILayout.Label("    off: " + off, _off);
            GUILayout.Label(Haptics.Path.Length > 0 ? "Path: " + Haptics.Path : "Path: " + (Haptics.Status.Length > 0 ? Haptics.Status : "not found yet"), _hint);
            GUILayout.Label("Grip actuator: palm, grabs, slaps and rubs; trigger: your index fingertip and fingers in her; thumb: your thumb. " +
                            "Only Meta's runtime (Link / Air Link) drives the Quest Pro's trigger and thumb actuators; over Virtual Desktop everything plays on the main motor. Tests: Debug tab.", _hint);
        }

        void EmbodySection(Character c)
        {
            GUILayout.Label("Embody", _bold);
            string off = !c.Female || c.IsPlayer ? "only with the game's woman in the scene" : null;
            GUI.enabled = off == null;
            bool embOn = _p.EmbodyOn.Value;   // read before the toggle (and the view mode before its toolbar): the same rows in the Layout pass and the click pass
            bool on = GUILayout.Toggle(embOn, " Embody him: see through his eyes, his hands on your controllers");
            GUI.enabled = true;
            if (off == null && on != _p.EmbodyOn.Value) _p.EmbodyOn.Value = on;
            if (off != null) { GUILayout.Label("off: " + off, _off); return; }
            GUI.enabled = embOn;
            int partner = GUILayout.Toolbar(_p.EmbodyPartner.Value, Partners);
            if (partner != _p.EmbodyPartner.Value) _p.EmbodyPartner.Value = partner;
            GUILayout.Label("View", _hint);
            GUILayout.BeginHorizontal();
            int viewWas = _p.EmbodyView.Value;
            int view = GUILayout.Toolbar(viewWas, ViewModes);
            if (view != _p.EmbodyView.Value) _p.EmbodyView.Value = view;
            if (GUILayout.Button("Re-snap", GUILayout.Width(80))) _body.Embody.SnapView();
            GUILayout.EndHorizontal();
            bool hide = GUILayout.Toggle(_p.EmbodyHideHead.Value, " Hide his head");
            if (hide != _p.EmbodyHideHead.Value) _p.EmbodyHideHead.Value = hide;
            GUILayout.Label("His hands follow", _hint);
            int handsWas = _p.EmbodyHands.Value;   // read before the controls: the same rows in the Layout pass and the click pass
            int hands = GUILayout.Toolbar(handsWas, HandModes);
            if (hands != _p.EmbodyHands.Value) _p.EmbodyHands.Value = hands;
            string handOff = !embOn ? "Embody is off" : handsWas == 0 ? "his hands don't follow" : null;
            string elbowOff = handOff ?? (!(_p.Apply.Value && _p.Physics.Value) && !_p.HandsOn.Value ? "her body is not built (soft zones or physics hands)" : null);
            GUI.enabled = handOff == null;
            bool nf = GUILayout.Toggle(_p.EmbodyNeutralFingers.Value, " Neutral fingers: the grab button closes the hand, it stops on her");
            if (handOff == null && nf != _p.EmbodyNeutralFingers.Value) _p.EmbodyNeutralFingers.Value = nf;
            GUI.enabled = elbowOff == null;
            bool ea = GUILayout.Toggle(_p.EmbodyElbowAvoid.Value, " His elbow swings around her");
            if (elbowOff == null && ea != _p.EmbodyElbowAvoid.Value) _p.EmbodyElbowAvoid.Value = ea;
            GUI.enabled = true;
            if (embOn && elbowOff != null) GUILayout.Label("    off: " + elbowOff, _off);
            // view alignment (acts at once: a change re-snaps the view)
            string viewOff = !embOn ? "off: Embody is off" : viewWas == 0 ? "off: the view is the game's camera" : null;
            GUI.enabled = viewOff == null;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Face the view to", GUILayout.Width(170));
            int al = GUILayout.Toolbar(_p.EmbodyViewAlign.Value, ViewAligns);
            if (viewOff == null && al != _p.EmbodyViewAlign.Value) _p.EmbodyViewAlign.Value = al;
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            float vy = Slider("View turn", _p.EmbodyViewYaw.Value, -90f, 90f, "0", 0f, viewOff, " deg");
            if (viewOff == null && Mathf.Abs(vy - _p.EmbodyViewYaw.Value) > 0.01f) _p.EmbodyViewYaw.Value = vy;
            float vp = Slider("View tilt", _p.EmbodyViewPitch.Value, -45f, 45f, "0", 0f, viewOff, " deg");
            if (viewOff == null && Mathf.Abs(vp - _p.EmbodyViewPitch.Value) > 0.01f) _p.EmbodyViewPitch.Value = vp;
            float vr = Slider("View roll", _p.EmbodyViewRoll.Value, -45f, 45f, "0", 0f, viewOff, " deg");
            if (viewOff == null && Mathf.Abs(vr - _p.EmbodyViewRoll.Value) > 0.01f) _p.EmbodyViewRoll.Value = vr;
            // 0.11.7 (user): the view's position, not only its orientation
            float vf = Slider("View forward", _p.EmbodyViewForward.Value * 100f, -20f, 20f, "+0.0;-0.0;0", 0f, viewOff, " cm") / 100f;
            if (viewOff == null && Mathf.Abs(vf - _p.EmbodyViewForward.Value) > 0.0001f) _p.EmbodyViewForward.Value = vf;
            float vu = Slider("View up", _p.EmbodyViewUp.Value * 100f, -20f, 20f, "+0.0;-0.0;0", 0f, viewOff, " cm") / 100f;
            if (viewOff == null && Mathf.Abs(vu - _p.EmbodyViewUp.Value) > 0.0001f) _p.EmbodyViewUp.Value = vu;
            float vrt = Slider("View right", _p.EmbodyViewRight.Value * 100f, -20f, 20f, "+0.0;-0.0;0", 0f, viewOff, " cm") / 100f;
            if (viewOff == null && Mathf.Abs(vrt - _p.EmbodyViewRight.Value) > 0.0001f) _p.EmbodyViewRight.Value = vrt;
            if (embOn && _body.Embody.Status.Length > 0) GUILayout.Label(_body.Embody.Status, _body.Embody.Status.StartsWith("off") ? _off : _hint);
            GUILayout.Label("The game's own POV camera can end up elsewhere; 'Follow his eyes' keeps your view at his eyes (position only, " +
                            "you turn your own head). A snap faces you the way his chest (or head) faces; turn/tilt/roll correct what is left " +
                            "(+ turn = right, + tilt = nose up; tilting the world can feel odd, keep it small); forward / up / right move the view from his eyes in his head's axes. " +
                            "Embody is off at every game start ([Embody] OnAtStart), and his hands hide the controller models while they follow them. Hand offsets per controller: [Embody] in sfmodvr.sfmorph.cfg.", _hint);
        }

        void MorphControls(Character c)
        {
            GUILayout.Space(6);
            GUILayout.Label(c.Look.Morph, _bold);
            var set = MorphSet.Find(c.Look.Morph);
            bool two = set != null && set.TwoFrames;   // decided once, before any control: the same controls in the Layout pass and the click pass
            bool fonly = set != null && set.FaceOnly;
            const string fonlyOff = "off: face-only character (its body conversion is not usable: body and body skin stay hers)";
            float fw = Slider("Face", c.Look.MorphFace * 100f, 0f, 100f, "0", 100f, null, " %") / 100f;
            if (Mathf.Abs(fw - c.Look.MorphFace) > 0.0001f) { c.Look.MorphFace = fw; _body.Touch(c); }
            float bw = Slider("Body", (fonly ? 0f : c.Look.MorphBody) * 100f, 0f, 100f, "0", 100f,
                              fonly ? fonlyOff : two ? null : "off: this character has a single shape (Face drives all of it)", " %") / 100f;
            if (two && !fonly && Mathf.Abs(bw - c.Look.MorphBody) > 0.0001f) { c.Look.MorphBody = bw; _body.Touch(c); }
            GUILayout.Label("Face: the character's face and head. Body: her figure (lower it if the body looks too heavy in her animations).", _hint);
            bool j = GUILayout.Toggle(c.Look.MorphJoints, " Move her joints to the new shape (limbs bend in the right place)");
            if (j != c.Look.MorphJoints) { c.Look.MorphJoints = j; _body.Touch(c); }
            GUILayout.BeginHorizontal();
            bool sk = GUILayout.Toggle(c.Look.MorphSkin, " Face skin texture", GUILayout.Width(200));
            if (sk != c.Look.MorphSkin) { c.Look.MorphSkin = sk; _body.Touch(c); }
            GUI.enabled = !fonly;
            bool skb = GUILayout.Toggle(c.Look.MorphSkinBody && !fonly, " Body skin texture");
            GUI.enabled = true;
            if (!fonly && skb != c.Look.MorphSkinBody) { c.Look.MorphSkinBody = skb; _body.Touch(c); }
            GUILayout.EndHorizontal();
            // presets, applied after the row so the Layout and click passes draw the same controls
            GUILayout.BeginHorizontal();
            bool faceOnly = GUILayout.Button("Face only (body stays hers)");
            GUI.enabled = !fonly;
            bool full = GUILayout.Button("Full character") && !fonly;
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (faceOnly) { c.Look.MorphFace = 1f; c.Look.MorphBody = 0f; c.Look.MorphSkin = true; c.Look.MorphSkinBody = false; _body.Touch(c); }
            else if (full) { c.Look.MorphFace = 1f; c.Look.MorphBody = 1f; c.Look.MorphSkin = true; c.Look.MorphSkinBody = true; _body.Touch(c); }
            GUILayout.Label("Facial likeness only: Body 0 %, Face 100 %, face skin on, body skin off.", _hint);
            if (fonly) GUILayout.Label(fonlyOff, _off);
            BodySliders(c, set, two);
            if (!_p.Apply.Value) GUILayout.Label("off: 'Apply changes' is off", _off);
            else if (c.Morph.Status.Length > 0) GUILayout.Label(c.Morph.Status, c.Morph.Status.StartsWith("off") ? _off : _hint);
            GUILayout.Label("Clothes follow the skin by its nearest vertex and are pushed out where the new shape would poke through. " +
                            "Her height stays the game's (the VaM character is scaled to her eye height), so scenes still line up.", _hint);
        }

        // Body sliders (VaM morphs exported with the character), joint correctives, skin tone and gloss. The sliders scale with Body.
        void BodySliders(Character c, MorphSet set, bool two)
        {
            GUILayout.Space(6);
            GUILayout.Label("Body sliders", _bold);
            // decided once, before any control: the same controls in the Layout pass and the click pass
            const string reexport = "re-export the character from VaM with Export morphs, then run the pipeline";
            bool haveSliders = set != null && set.Sliders.Count > 0, haveJcm = set != null && set.Jcms.Count > 0;
            string slidersOff = !haveSliders ? "off: this character has no body slider data (" + reexport + ")"
                              : c.Look.MorphBody <= 0f || set.FaceOnly ? "off: Body is 0 % (the sliders scale with it)" : null;
            if (haveSliders)
                foreach (var sd in set.Sliders)
                {
                    float s;
                    if (!c.Look.Sliders.TryGetValue(sd.Name, out s)) s = sd.S0;
                    float nv = Slider(sd.Name, s, 0f, 1f, "0.00", sd.S0, slidersOff);
                    if (slidersOff == null && Mathf.Abs(nv - s) > 0.0001f)
                    {
                        if (Mathf.Abs(nv - sd.S0) < 0.005f) c.Look.Sliders.Remove(sd.Name); else c.Look.Sliders[sd.Name] = nv;
                        _body.Touch(c);
                    }
                }
            else GUILayout.Label("    " + slidersOff, _off);
            float tone = Slider("Skin tone", c.Look.SkinTone, -0.1f, 0.1f, "+0.00;-0.00;0", 0f);
            if (Mathf.Abs(tone - c.Look.SkinTone) > 0.0001f) { c.Look.SkinTone = Mathf.Abs(tone) < 0.0025f ? 0f : tone; _body.Touch(c); }
            float gloss = Slider("Gloss", c.Look.Gloss, 0f, 2f, "0.00", 1f);
            if (Mathf.Abs(gloss - c.Look.Gloss) > 0.0001f) { c.Look.Gloss = Mathf.Abs(gloss - 1f) < 0.01f ? 1f : gloss; _body.Touch(c); }
            GUI.enabled = haveJcm;
            bool jcm = GUILayout.Toggle(c.Look.MorphJcm, " Joint correctives (shapes that grow as her joints bend)");
            bool angles = GUILayout.Toggle(c.Morph.ShowAngles, " Show corrective angles (log, once a second)");
            GUI.enabled = true;
            if (haveJcm)
            {
                if (jcm != c.Look.MorphJcm) { c.Look.MorphJcm = jcm; _body.Touch(c); }
                if (angles != c.Morph.ShowAngles) c.Morph.ShowAngles = angles;
            }
            else GUILayout.Label("    off: this character has no corrective data (" + reexport + ")", _off);
            GUILayout.Label("Sliders move a body area away from the character's own default (Reset goes back to it). " +
                            "Skin tone lightens or darkens the skin tint; Gloss sets her whole skin's shine (0 matte, 1 the game's, 2 wet).", _hint);
        }

        void HandsSection(Character c)
        {
            GUILayout.Label("VR hands", _bold);
            string off = !c.Female || c.IsPlayer ? "only with the game's woman in the scene" : null;
            // what the gating reads is taken before any control here changes the config: the same controls in the Layout pass and the click pass
            bool handsOn = _p.HandsOn.Value, collide = _p.HandsCollide.Value;
            GUI.enabled = off == null;
            bool on = GUILayout.Toggle(handsOn, " Physics hands instead of the controller models (they collide and press her skin)");
            GUI.enabled = true;
            if (off == null && on != _p.HandsOn.Value) _p.HandsOn.Value = on;
            if (off != null) { GUILayout.Label("off: " + off, _off); return; }
            string handsOff = handsOn ? null : "physics hands are off";
            string hitOff = handsOff ?? (collide ? null : "collisions are off");   // what only matters while her body can be hit
            GUI.enabled = handsOn;
            int mesh = GUILayout.Toolbar(_p.HandsMesh.Value, HandMeshes);
            if (mesh != _p.HandsMesh.Value) _p.HandsMesh.Value = mesh;
            bool col = GUILayout.Toggle(collide, " Stop at her body (the skin gives first, then resistance)");
            if (col != _p.HandsCollide.Value) _p.HandsCollide.Value = col;
            // her body's collision shapes: hulls follow the skin's cross-section, capsules are the 0.7.7 shapes
            GUI.enabled = hitOff == null;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Body colliders", GUILayout.Width(170));
            int bc = GUILayout.Toolbar(_p.HandsHull.Value ? 0 : 1, BodyColliders);
            if (hitOff == null && (bc == 0) != _p.HandsHull.Value) _p.HandsHull.Value = bc == 0;
            GUILayout.EndHorizontal();
            if (hitOff != null) { GUI.enabled = true; GUILayout.Label("    off: " + hitOff, _off); }
            GUI.enabled = handsOn;
            bool jf = GUILayout.Toggle(_p.HandsJointed.Value, " Jointed fingers (simulated finger bodies that bend on her skin; off: posed by the grip only)");
            if (handsOn && jf != _p.HandsJointed.Value) _p.HandsJointed.Value = jf;
            GUI.enabled = true;   // the grab button also closes Embody's hands, physics hands on or off
            GUILayout.BeginHorizontal();
            GUILayout.Label("Grab with", GUILayout.Width(170));
            int gb = GUILayout.Toolbar(_p.HandsGrabButton.Value, GrabButtons);
            if (gb != _p.HandsGrabButton.Value) _p.HandsGrabButton.Value = gb;
            GUILayout.EndHorizontal();
            GUI.enabled = hitOff == null;
            bool fc = GUILayout.Toggle(_p.HandsFingerConform.Value, " Fingers close on her skin and sink in by softness");
            if (hitOff == null && fc != _p.HandsFingerConform.Value) _p.HandsFingerConform.Value = fc;
            GUI.enabled = handsOn;
            bool dfade = GUILayout.Toggle(_p.HandsDrawnFade.Value, " Drawn hand is exactly the controller in free air (physics offset fades in on contact)");
            if (handsOn && dfade != _p.HandsDrawnFade.Value) _p.HandsDrawnFade.Value = dfade;
            if (handsOff != null) { GUI.enabled = true; GUILayout.Label("    off: " + handsOff, _off); }
            else if (hitOff != null) { GUI.enabled = true; GUILayout.Label("    finger conform off: " + hitOff, _off); }
            string restOff = handsOn || (_p.EmbodyOn.Value && _p.EmbodyHands.Value != 0) ? null : "off: physics hands and Embody hands are both off";
            float rc = Slider("Rest curl", _p.HandsRestCurl.Value * 100f, 0f, 100f, "0", 25f, restOff, " %") / 100f;
            if (restOff == null && Mathf.Abs(rc - _p.HandsRestCurl.Value) > 0.0001f) _p.HandsRestCurl.Value = rc;
            GUI.enabled = handsOn;
            float fr = Slider("Grip friction", _p.HandsFriction.Value * 100f, 0f, 100f, "0", 80f, hitOff == null ? null : "off: " + hitOff, " %") / 100f;
            if (hitOff == null && Mathf.Abs(fr - _p.HandsFriction.Value) > 0.0001f) _p.HandsFriction.Value = fr;
            GUI.enabled = true;
            if (handsOn && PhysicsHands.Status.Length > 0) GUILayout.Label(PhysicsHands.Status, PhysicsHands.Status.StartsWith("off") ? _off : _hint);
            GUILayout.Label("The grab button closes the whole hand (it stops on her skin; the grip is the game's menu button, so the trigger is the default; Embody's hands too). Rest curl: the hand with nothing pressed. Friction: a resting hand is carried along when she moves. " +
                            "Hand position/rotation offsets per controller: [Embody] Left/RightHandOffset in sfmodvr.sfmorph.cfg.", _hint);
        }

        // The hands that touch her, whichever hands they are: yours (controller models or physics hands), the partner's, her own.
        void ContactsSection(Character c)
        {
            GUILayout.Label("Contacts", _bold);
            bool touch = (_p.Apply.Value && _p.Physics.Value) || _p.HandsOn.Value;   // something reads the contacts
            string who = !c.Female || c.IsPlayer ? "only for the game's woman" : null;
            string off = who ?? (touch ? null : "soft zones and physics hands are off");
            GUI.enabled = off == null;
            bool gate = GUILayout.Toggle(_p.GateHidden.Value, " Hidden hands don't touch her (partner not drawn, controller models replaced)");
            GUI.enabled = true;
            if (off == null && gate != _p.GateHidden.Value) _p.GateHidden.Value = gate;
            if (off != null) GUILayout.Label("    off: " + off, _off);
            if (who == null && VrHands.Status.Length > 0)
            {
                GUILayout.Label(VrHands.Status, _hint);
                GUILayout.Label(VrHands.ContactsFeed, _hint);
            }
            GUILayout.Label("Contacts come from your VR hands (or your physics hands), the partner's hands and fingers, her forearms and hands, and her other thigh. " +
                            "They press her soft zones and push her limbs. A hand that isn't drawn is left out when the toggle is on.", _hint);
        }

        // Her limbs, head and spine give way to hands that press into them and spring back.
        void PushesSection(Character c)
        {
            GUILayout.Label("Pushes", _bold);
            // her body segments are only built while soft zones or the physics hands are on (Body.ApplyAll): without them nothing can push
            string off = !_p.Apply.Value ? "'Apply changes' is off" : !c.Female || c.IsPlayer ? "only for the game's woman"
                       : !_p.Physics.Value && !_p.HandsOn.Value ? "soft zones and physics hands are both off (no hand is tracked against her)" : null;
            float push = _p.HandsLimbPush.Value;   // read before the controls: the same rows in the Layout pass and the click pass
            float lp = Slider("Her limbs yield to hands", push * 100f, 0f, 100f, "0", 50f, off == null ? null : "off: " + off, " %") / 100f;
            if (off == null && Mathf.Abs(lp - _p.HandsLimbPush.Value) > 0.0001f) _p.HandsLimbPush.Value = lp;
            string byOff = off ?? (push > 0f ? null : "push is 0");
            bool byTouch = _p.HandsPushByTouch.Value;
            string partnerOff = byOff ?? (byTouch ? null : "other hands don't push");
            GUI.enabled = byOff == null;
            bool touch = GUILayout.Toggle(byTouch, " Other hands push too (your hand models, the partner you embody), by how deep they are");
            GUI.enabled = partnerOff == null;
            bool partner = GUILayout.Toggle(_p.HandsPushByPartner.Value, "   ...and the partner's animated hands (not the one you embody: his hands are your controllers; off keeps her in his grip)");
            GUI.enabled = true;
            if (byOff == null && touch != _p.HandsPushByTouch.Value) _p.HandsPushByTouch.Value = touch;
            if (partnerOff == null && partner != _p.HandsPushByPartner.Value) _p.HandsPushByPartner.Value = partner;
            GUI.enabled = byOff == null;
            bool ylock = GUILayout.Toggle(_p.HandsYieldLock.Value, " Lock her hands and feet while a limb yields (the elbow / knee bends, the hand / foot stays put)");
            GUI.enabled = true;
            if (byOff == null && ylock != _p.HandsYieldLock.Value) _p.HandsYieldLock.Value = ylock;
            if (byOff != null) GUILayout.Label("    off: " + byOff, _off);
            if (off == null && HerBody.Status.Length > 0) GUILayout.Label(HerBody.Status, HerBody.Status.StartsWith("off") ? _off : _hint);
            GUILayout.Label("A hand that presses into an arm, leg, her head or torso moves it aside; it settles back without bouncing (never past 35/40 deg " +
                            "at the shoulder/elbow, 20/25 at the hip/knee, 15 head, 8 spine). Measured from where the hand is, not from the blocked physics hand.", _hint);
        }

        static readonly string[] GrabButtons = { "Trigger", "Grip", "Either" };
        static readonly string[] ViewAligns = { "His head", "His chest" };
        static readonly string[] Qualities = { "Performance", "Balanced", "Fidelity" };
        static readonly string[] BreastModes = { "Off", "Main chains", "All" };

        // Her secondary motion: limbs, head and spine trail the animation through damped springs.
        void MotionSection(Character c)
        {
            GUILayout.Label("Secondary motion", _bold);
            string off = !_p.Apply.Value ? "'Apply changes' is off" : !c.Female || c.IsPlayer ? "only for the game's woman" : null;
            float a = Slider("Overlap", _p.OverlapAmount.Value * 100f, 0f, 100f, "0", 30f, off == null ? null : "off: " + off, " %") / 100f;
            if (Mathf.Abs(a - _p.OverlapAmount.Value) > 0.0001f) _p.OverlapAmount.Value = a;
            string sub = off ?? (_p.OverlapAmount.Value > 0f ? null : "overlap is 0");
            string subOff = sub == null ? null : "off: " + sub;
            float str = Slider("Strength", _p.OverlapStrength.Value, 1f, 4f, "0.0", 1f, subOff, " x");
            if (subOff == null && Mathf.Abs(str - _p.OverlapStrength.Value) > 0.0001f) _p.OverlapStrength.Value = str;
            float v;
            v = Slider("Ripple", _p.OverlapRipple.Value * 100f, 0f, 100f, "0", 50f, subOff, " %") / 100f;
            if (Mathf.Abs(v - _p.OverlapRipple.Value) > 0.0001f) _p.OverlapRipple.Value = v;
            v = Slider("  Arms", _p.OverlapArms.Value * 100f, 0f, 100f, "0", 100f, subOff, " %") / 100f;
            if (Mathf.Abs(v - _p.OverlapArms.Value) > 0.0001f) _p.OverlapArms.Value = v;
            v = Slider("    bounce", _p.ArmsBounce.Value * 100f, 0f, 100f, "0", 50f, subOff, " %") / 100f;
            if (Mathf.Abs(v - _p.ArmsBounce.Value) > 0.0001f) _p.ArmsBounce.Value = v;
            v = Slider("  Legs", _p.OverlapLegs.Value * 100f, 0f, 100f, "0", 100f, subOff, " %") / 100f;
            if (Mathf.Abs(v - _p.OverlapLegs.Value) > 0.0001f) _p.OverlapLegs.Value = v;
            v = Slider("    bounce", _p.LegsBounce.Value * 100f, 0f, 100f, "0", 40f, subOff, " %") / 100f;
            if (Mathf.Abs(v - _p.LegsBounce.Value) > 0.0001f) _p.LegsBounce.Value = v;
            v = Slider("  Head & neck", _p.OverlapHead.Value * 100f, 0f, 100f, "0", 100f, subOff, " %") / 100f;
            if (Mathf.Abs(v - _p.OverlapHead.Value) > 0.0001f) _p.OverlapHead.Value = v;
            v = Slider("    bounce", _p.HeadBounce.Value * 100f, 0f, 100f, "0", 60f, subOff, " %") / 100f;
            if (Mathf.Abs(v - _p.HeadBounce.Value) > 0.0001f) _p.HeadBounce.Value = v;
            v = Slider("  Spine", _p.OverlapSpine.Value * 100f, 0f, 100f, "0", 100f, subOff, " %") / 100f;
            if (Mathf.Abs(v - _p.OverlapSpine.Value) > 0.0001f) _p.OverlapSpine.Value = v;
            v = Slider("    bounce", _p.SpineBounce.Value * 100f, 0f, 100f, "0", 30f, subOff, " %") / 100f;
            if (Mathf.Abs(v - _p.SpineBounce.Value) > 0.0001f) _p.SpineBounce.Value = v;
            GUI.enabled = sub == null;
            bool planted = GUILayout.Toggle(_p.OverlapPlanted.Value, " Planted hands and feet stay where the animation puts them (on her, on him, on the bed; a fast free swing lets go)");
            GUI.enabled = true;
            if (sub == null && planted != _p.OverlapPlanted.Value) _p.OverlapPlanted.Value = planted;
            if (off == null && _body.Overlap.Status.Length > 0) GUILayout.Label(_body.Overlap.Status, _body.Overlap.Status.StartsWith("off") ? _off : _hint);
            GUILayout.Label("Her elbows, wrists, knees, head and spine trail fast animation a little and settle (follow-through). " +
                            "It fades out where a hand or her other leg touches her, so contacts don't slide. " +
                            "Ripple: inertia down the chains (turns and lurches of the torso swing the head and arms a little later, even with Spine at 0). " +
                            "Bounce: how much a part overshoots and settles.", _hint);
        }

        void PhysicsSection(Character c)
        {
            GUILayout.Label("Soft zones", _bold);
            string off = !_p.Apply.Value ? "'Apply changes' is off"
                       : !c.Female || c.IsPlayer ? "only for the game's woman" : null;
            GUI.enabled = off == null;
            bool on = GUILayout.Toggle(_p.Physics.Value, " Soft zones: hands and limbs press into her glutes, thighs, belly and breasts");
            GUI.enabled = true;
            if (off == null && on != _p.Physics.Value) _p.Physics.Value = on;
            string sliderOff = off ?? (_p.Physics.Value ? null : "soft zones are off");
            float s = Slider("Softness", _p.Softness.Value * 100f, 0f, 100f, "0", 50f, sliderOff == null ? null : "off: " + sliderOff, " %") / 100f;
            if (Mathf.Abs(s - _p.Softness.Value) > 0.0001f) _p.Softness.Value = s;
            float gv = Slider("Gravity", _p.Gravity.Value * 100f, 0f, 100f, "0", 50f, sliderOff == null ? null : "off: " + sliderOff, " %") / 100f;
            if (Mathf.Abs(gv - _p.Gravity.Value) > 0.0001f) _p.Gravity.Value = gv;
            float sp = Slider("Springiness", _p.Springiness.Value * 100f, 0f, 100f, "0", 30f, sliderOff == null ? null : "off: " + sliderOff, " %") / 100f;
            if (Mathf.Abs(sp - _p.Springiness.Value) > 0.0001f) _p.Springiness.Value = sp;
            GUI.enabled = sliderOff == null;
            GUILayout.Label("Quality", _hint);
            int q = GUILayout.Toolbar(Mathf.Clamp(_p.Quality.Value, 0, 2), Qualities);
            if (sliderOff == null && q != _p.Quality.Value) _p.Quality.Value = q;
            bool gpu = GUILayout.Toggle(_p.GpuContactOn.Value, " Per-vertex contact on the GPU");
            if (sliderOff == null && gpu != _p.GpuContactOn.Value) _p.GpuContactOn.Value = gpu;
            if (sliderOff == null && _p.GpuContactOn.Value && _body.GpuStatus.Length > 0) GUILayout.Label(_body.GpuStatus, _body.GpuStatus.Contains("off:") ? _off : _hint);
            GUI.enabled = true;
            ContactVisualRows(sliderOff);
            GUI.enabled = sliderOff == null;
            GUILayout.Label("Breast physics colliders (hands push the game's own breast physics)", _hint);
            int bc = GUILayout.Toolbar(Mathf.Clamp(_p.BreastColliders.Value, 0, 2), BreastModes);
            if (sliderOff == null && bc != _p.BreastColliders.Value) _p.BreastColliders.Value = bc;
            GUI.enabled = true;
            if (off != null) GUILayout.Label("off: " + off, _off);
            else
            {
                if (c.Soft.Status.Length > 0) GUILayout.Label(c.Soft.Status, c.Soft.Status.StartsWith("off") ? _off : _hint);
                if (c.Soft.Contacts.Length > 0) GUILayout.Label(c.Soft.Contacts, _hint);
            }
            GUILayout.Label("Soft points under her skin (springs, like VaM's soft physics) are pushed by your VR hands, the partner's hands and fingers, " +
                            "the partner's pelvis and thighs, and her own arms, hands, thighs and calves. Softness: how far the skin gives (each part by its anatomy). Springiness: how much the zones lag " +
                            "and settle as she moves. Works with or without a character shape. Which hands count: VR tab, Contacts.", _hint);
        }

        // 0.8.0 contact visuals v2: the GPU dent's edge, rim, self-contact, partner body, bend bulges, ripple and skin marks.
        void ContactVisualRows(string sliderOff)
        {
            string gpuOff = sliderOff ?? (!_p.GpuContactOn.Value ? "GPU contact is off" : _body.Gpu == null ? "shader bundle not loaded" : null);
            string so = gpuOff == null ? null : "off: " + gpuOff;
            bool en = gpuOff == null;
            float v = Slider("  Dent edge", _p.ContactSkirt.Value * 1000f, 5f, 40f, "0", 15f, so, " mm") / 1000f;
            if (Mathf.Abs(v - _p.ContactSkirt.Value) > 0.00001f) _p.ContactSkirt.Value = v;
            v = Slider("  Rim bulge", _p.ContactBulge.Value * 100f, 0f, 100f, "0", 60f, so, " %") / 100f;
            if (Mathf.Abs(v - _p.ContactBulge.Value) > 0.0001f) _p.ContactBulge.Value = v;
            v = Slider("  Dent depth", _p.ContactDepth.Value * 100f, 30f, 200f, "0", 100f, so, " %") / 100f;
            if (Mathf.Abs(v - _p.ContactDepth.Value) > 0.0001f) _p.ContactDepth.Value = v;
            GUI.enabled = en;
            bool nrm = GUILayout.Toggle(_p.ContactNormals.Value, " Shading follows the dent (pressed-flesh lighting)");
            bool self = GUILayout.Toggle(_p.SelfContact.Value, " Her own limbs press her skin (thighs together, calf on thigh, arm on breast)");
            bool pb = GUILayout.Toggle(_p.PartnerBody.Value, " The partner's body presses her too (pelvis, thighs)");
            GUI.enabled = true;
            if (en && nrm != _p.ContactNormals.Value) _p.ContactNormals.Value = nrm;
            v = Slider("    shading smoothness", _p.ContactNormalSmooth.Value, 0f, 20f, "0", 8f, so ?? (_p.ContactNormals.Value ? null : "off: shading does not follow the dent"), " mm");
            if (Mathf.Abs(v - _p.ContactNormalSmooth.Value) > 0.001f) _p.ContactNormalSmooth.Value = v;
            if (en && self != _p.SelfContact.Value) _p.SelfContact.Value = self;
            if (en && pb != _p.PartnerBody.Value) _p.PartnerBody.Value = pb;
            // 0.11.7: ripples from his strokes
            GUI.enabled = en && _p.PartnerBody.Value;
            bool rt = GUILayout.Toggle(_p.RippleThrust.Value, "    ...and each stroke of his hips sends a ripple through her");
            GUI.enabled = true;
            if (en && _p.PartnerBody.Value && rt != _p.RippleThrust.Value) _p.RippleThrust.Value = rt;
            v = Slider("    stroke ripple", _p.RippleThrustAmt.Value * 100f, 0f, 300f, "0", 100f, so ?? (!_p.PartnerBody.Value ? "off: the partner's body is off" : !_p.RippleThrust.Value ? "off: strokes are off" : null), " %") / 100f;
            if (Mathf.Abs(v - _p.RippleThrustAmt.Value) > 0.0001f) _p.RippleThrustAmt.Value = v;
            // 0.11.28: Delta Mush at her joints
            string mo = so ?? (_body.Gpu != null && _body.Gpu.MushNote.Length > 0 ? "off: " + _body.Gpu.MushNote : null);
            GUI.enabled = mo == null;
            bool mush = GUILayout.Toggle(_p.DeltaMush.Value, " Round joints in deep bends (Delta Mush: no pinching at elbows, knees, hips, shoulders)");
            GUI.enabled = true;
            if (mo == null && mush != _p.DeltaMush.Value) _p.DeltaMush.Value = mush;
            if (mo != null) GUILayout.Label("    " + mo, _off);
            v = Slider("    joint smoothing", _p.DeltaMushAmount.Value * 100f, 0f, 100f, "0", 100f, mo ?? (_p.DeltaMush.Value ? null : "off: round joints is off"), " %") / 100f;
            if (Mathf.Abs(v - _p.DeltaMushAmount.Value) > 0.0001f) _p.DeltaMushAmount.Value = v;
            if (mo == null && _p.DeltaMush.Value)   // 0.11.47: per joint and the volume keeping (shown only while it runs)
            {
                MushRow("      shoulders", _p.MushShoulder); MushRow("      elbows", _p.MushElbow); MushRow("      hips", _p.MushHip); MushRow("      knees", _p.MushKnee);
                MushRow("    keep volume", _p.DeltaMushKeepVolume);
            }
            // 0.11.31 skin folds at deep bends
            string fo = so ?? (_body.Gpu != null && !_body.Gpu.FoldKernels ? "off: the shader bundle is older than this DLL (install dist/sfmorph_shaders)" : null);
            v = Slider("  Skin folds", _p.SkinFolds.Value * 100f, 0f, 200f, "0", 100f, fo, " %") / 100f;
            if (Mathf.Abs(v - _p.SkinFolds.Value) > 0.0001f) _p.SkinFolds.Value = v;
            string fo2 = fo ?? (_p.SkinFolds.Value > 0f ? null : "off: skin folds are 0");
            v = Slider("    inner elbow", _p.FoldElbowMm.Value, 0f, 15f, "0.0", 4f, fo2, " mm");
            if (Mathf.Abs(v - _p.FoldElbowMm.Value) > 0.001f) _p.FoldElbowMm.Value = v;
            v = Slider("    back of the knee", _p.FoldKneeMm.Value, 0f, 15f, "0.0", 5f, fo2, " mm");
            if (Mathf.Abs(v - _p.FoldKneeMm.Value) > 0.001f) _p.FoldKneeMm.Value = v;
            v = Slider("    groin (sitting)", _p.FoldHipMm.Value, 0f, 15f, "0.0", 5f, fo2, " mm");
            if (Mathf.Abs(v - _p.FoldHipMm.Value) > 0.001f) _p.FoldHipMm.Value = v;
            v = Slider("  Bend bulges", _p.BendBulge.Value * 100f, 0f, 100f, "0", 60f, so, " %") / 100f;
            if (Mathf.Abs(v - _p.BendBulge.Value) > 0.0001f) _p.BendBulge.Value = v;
            // 0.11.23: the waist roll on the side she bends toward
            v = Slider("    waist roll in a side bend", _p.BendWaistRollMm.Value, 0f, 20f, "0", 10f, so ?? (_p.BendBulge.Value > 0f ? null : "off: bend bulges are 0"), " mm");
            if (Mathf.Abs(v - _p.BendWaistRollMm.Value) > 0.001f) _p.BendWaistRollMm.Value = v;
            v = Slider("  Ripple on impact", _p.RippleAmount.Value * 100f, 0f, 200f, "0", 100f, so, " %") / 100f;
            if (Mathf.Abs(v - _p.RippleAmount.Value) > 0.0001f) _p.RippleAmount.Value = v;
            // 0.9.4: the wave's speed, wavelength and life were config-only
            string ro = so ?? (_p.RippleAmount.Value > 0f ? null : "off: ripple is 0");
            v = Slider("    speed", _p.RippleSpeed.Value, 0.2f, 3f, "0.00", 0.5f, ro, " m/s");
            if (Mathf.Abs(v - _p.RippleSpeed.Value) > 0.0001f) _p.RippleSpeed.Value = v;
            v = Slider("    wavelength", _p.RippleWavelength.Value * 100f, 3f, 20f, "0.0", 10f, ro, " cm") / 100f;
            if (Mathf.Abs(v - _p.RippleWavelength.Value) > 0.00001f) _p.RippleWavelength.Value = v;
            v = Slider("    duration", _p.RippleDamping.Value, 0.1f, 2f, "0.00", 0.6f, ro, " s");
            if (Mathf.Abs(v - _p.RippleDamping.Value) > 0.0001f) _p.RippleDamping.Value = v;
            // 0.9.7 (user: the first hit is too spiky, the spreading rings are a good height)
            v = Slider("    impact dip", _p.RippleImpact.Value * 100f, 0f, 100f, "0", 35f, ro, " %") / 100f;
            if (Mathf.Abs(v - _p.RippleImpact.Value) > 0.0001f) _p.RippleImpact.Value = v;
            if (en)
            {
                if (BendPose.Status.Length > 0) GUILayout.Label(BendPose.Status, _hint);
                if (Ripple.Status.Length > 0) GUILayout.Label(Ripple.Status, _hint);
            }
            GUILayout.Label("Dent edge: how far around a hand the skin follows the dent (5 mm = the old hard edge). Rim bulge: the pressed flesh rises " +
                            "around it. Bend bulges: biceps, thighs and calves thicken as the joints bend, her belly rounds forward when she sits or squats. " +
                            "Ripple: a slap or a thrust sends a wave through her glutes, thighs and breasts (speed: how fast it travels; wavelength: crest " +
                            "to crest, a longer one reaches farther; duration: seconds to fade to a third; impact dip: how deep the first dip under the hand is next to the rings). " +
                            "Skin marks: Skin tab.", _hint);
        }

        // Skin marks (hand prints, slap redness): drawn from the GPU contact, so they need it on (Physics tab)
        void MarksSection(Character c)
        {
            GUILayout.Label("Skin marks", _bold);
            string off = !_p.Apply.Value ? "'Apply changes' is off" : !c.Female || c.IsPlayer ? "only for the game's woman"
                       : !_p.Physics.Value ? "soft zones are off (Physics tab)" : null;
            string gpuOff = off ?? (!_p.GpuContactOn.Value ? "GPU contact is off (Physics tab)" : _body.Gpu == null ? "shader bundle not loaded" : null);
            string so = gpuOff == null ? null : "off: " + gpuOff;
            bool en = gpuOff == null;
            float v;
            GUI.enabled = en;
            bool mk = GUILayout.Toggle(_p.SkinMarksOn.Value, " Marks: hand prints and slap redness");
            GUI.enabled = true;
            if (en && mk != _p.SkinMarksOn.Value) _p.SkinMarksOn.Value = mk;
            string mo = so ?? (_p.SkinMarksOn.Value ? null : "off: marks are off");
            v = Slider("    fade", _p.MarksFade.Value, 3f, 60f, "0", 15f, mo, " s");
            if (Mathf.Abs(v - _p.MarksFade.Value) > 0.001f) _p.MarksFade.Value = v;
            v = Slider("    redness", _p.MarksRedness.Value * 100f, 0f, 100f, "0", 50f, mo, " %") / 100f;
            if (Mathf.Abs(v - _p.MarksRedness.Value) > 0.0001f) _p.MarksRedness.Value = v;
            v = Slider("    boost", _p.MarksBoost.Value, 1f, 4f, "0.0", 1f, mo, " x");
            if (Mathf.Abs(v - _p.MarksBoost.Value) > 0.001f) _p.MarksBoost.Value = v;
            // 0.11.20 (user: the dent shading and the hand print are polygonal, the print lasts too long and has no setting)
            v = Slider("    press shade", _p.MarksAo.Value * 100f, 0f, 100f, "0", 50f, mo, " %") / 100f;
            if (Mathf.Abs(v - _p.MarksAo.Value) > 0.0001f) _p.MarksAo.Value = v;
            v = Slider("    print", _p.MarksPrint.Value * 100f, 0f, 150f, "0", 60f, mo, " %") / 100f;
            if (Mathf.Abs(v - _p.MarksPrint.Value) > 0.0001f) _p.MarksPrint.Value = v;
            v = Slider("    print fade", _p.MarksPrintTime.Value, 0.2f, 5f, "0.0", 0.8f, mo, " s");
            if (Mathf.Abs(v - _p.MarksPrintTime.Value) > 0.001f) _p.MarksPrintTime.Value = v;
            v = Slider("    smoothing", _p.MarksSmooth.Value, 0f, 15f, "0.0", 6f, mo, " mm");
            if (Mathf.Abs(v - _p.MarksSmooth.Value) > 0.001f) _p.MarksSmooth.Value = v;
            if (!en) GUILayout.Label("    " + so, _off);
            else if (_body.Marks.Status.Length > 0) GUILayout.Label(_body.Marks.Status, _body.Marks.Status.Contains("off:") ? _off : _hint);
            GUILayout.Label("Marks come from hands, the partner and slaps (her face and neck too, lighter); her own limbs dent her skin but never redden it. " +
                            "Fade: seconds for redness to fade to a third. Boost multiplies the build-up, redness and press shade. Press shade: how much a live press darkens " +
                            "the skin. Print: how deep the print of a lifted hand shows (share of the press); print fade: seconds for it to fade to a third. " +
                            "Smoothing: how far the marks are rounded off on her skin (0 = they follow the mesh's triangles).", _hint);
        }

        static readonly string[] WrinkleDebugs = { "Off", "Activation", "Regions" };

        void WrinklesSection(Character c)
        {
            GUILayout.Label("Skin wrinkles", _bold);
            string off = !_p.Apply.Value ? "'Apply changes' is off"
                       : !c.Female || c.IsPlayer ? "only for the game's woman" : null;
            GUI.enabled = off == null;
            bool on = GUILayout.Toggle(_p.WrinkOn.Value, " Wrinkles: expression lines and joint creases where her skin compresses");
            GUI.enabled = true;
            if (off == null && on != _p.WrinkOn.Value) _p.WrinkOn.Value = on;
            string sliderOff = off ?? (_p.WrinkOn.Value ? null : "wrinkles are off");
            string why = sliderOff == null ? null : "off: " + sliderOff;
            float st = Slider("Strength", _p.WrinkStrength.Value, 0f, 2f, "0.00", 1f, why);
            if (Mathf.Abs(st - _p.WrinkStrength.Value) > 0.0001f) _p.WrinkStrength.Value = st;
            float th = Slider("Threshold", _p.WrinkThreshold.Value, 0.01f, 0.06f, "0.000", 0.025f, why);
            if (Mathf.Abs(th - _p.WrinkThreshold.Value) > 0.00001f) _p.WrinkThreshold.Value = th;
            string cavOff = why ?? (_body.Wrink != null && !_body.Wrink.HasCavity ? "off: the shader bundle is older than this DLL (install dist/sfmorph_shaders)" : null);
            float cv = Slider("Crease shade", _p.WrinkCavity.Value, 0f, 1f, "0.00", 0.3f, cavOff);
            if (Mathf.Abs(cv - _p.WrinkCavity.Value) > 0.0001f) _p.WrinkCavity.Value = cv;
            // 0.11.47: curve-driven face lines (lip sync curves, face-clip moods)
            GUI.enabled = sliderOff == null;
            bool cdr = GUILayout.Toggle(_p.WrinkCurveDrive.Value, " Expression curves: face lines follow her lip sync and face-clip moods too");
            GUI.enabled = true;
            if (sliderOff == null && cdr != _p.WrinkCurveDrive.Value) _p.WrinkCurveDrive.Value = cdr;
            float cgn = Slider("Curve gain", _p.WrinkCurveGain.Value, 0f, 2f, "0.00", 1f, why ?? (_p.WrinkCurveDrive.Value ? null : "off: expression curves are off"));
            if (Mathf.Abs(cgn - _p.WrinkCurveGain.Value) > 0.0001f) _p.WrinkCurveGain.Value = cgn;
            GUI.enabled = sliderOff == null;
            GUILayout.BeginHorizontal();
            bool face = GUILayout.Toggle(_p.WrinkFace.Value, " Face", GUILayout.Width(90));
            bool neck = GUILayout.Toggle(_p.WrinkNeck.Value, " Neck", GUILayout.Width(90));
            bool body = GUILayout.Toggle(_p.WrinkBody.Value, " Body (joint creases)");
            GUILayout.EndHorizontal();
            if (sliderOff == null)
            {
                if (face != _p.WrinkFace.Value) _p.WrinkFace.Value = face;
                if (neck != _p.WrinkNeck.Value) _p.WrinkNeck.Value = neck;
                if (body != _p.WrinkBody.Value) _p.WrinkBody.Value = body;
            }
            GUI.enabled = sliderOff == null && _body.Wrink != null;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Recapture", GUILayout.Width(110))) _body.RequestWrinkleRecapture();
            GUILayout.Label("re-reads her resting skin (after changing a body slider or shape)", _hint);
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            if (off != null) GUILayout.Label("off: " + off, _off);
            else if (_p.WrinkOn.Value && _body.WrinkleStatus.Length > 0) GUILayout.Label(_body.WrinkleStatus, _body.WrinkleStatus.Contains("off:") ? _off : _hint);
            GUILayout.Label("Authored lines and creases (forehead, frown, crow's feet, under the eyes, smile lines, nose, neck; inside the elbows, " +
                            "behind the knees, hip folds, armpits, belly) fade in when her skin compresses across them, measured on the GPU. " +
                            "Nowhere else, so breasts and shoulders stay smooth. Threshold: how much it has to compress first. Crease shade darkens " +
                            "the bottom of each line in her skin colour (her skin's subsurface scattering blurs the normal-map shading, most of all " +
                            "on her face). Debug: Activation tints active lines red, Regions shows every region in its own colour. " +
                            "Speed and texture size are in the config file ([Wrinkles]).", _hint);
        }

        static readonly string[] MuscleDebugs = { "Off", "Tension", "Muscles" };
        static readonly string[] MuscleDrivers = { "Angles", "Lengths + loads", "Both" };   // 0.11.47 [Muscles] Driver

        // 0.10.0 muscle definition: a layer of the wrinkle sheets (same textures, same shader bundle), its own switch
        void MusclesSection(Character c)
        {
            GUILayout.Label("Muscle definition", _bold);
            string off = !_p.Apply.Value ? "'Apply changes' is off"
                       : !c.Female || c.IsPlayer ? "only for the game's woman"
                       : _body.Wrink != null && !_body.Wrink.HasMuscles ? "the shader bundle is older than this DLL (install dist/sfmorph_shaders)" : null;
            GUI.enabled = off == null;
            bool on = GUILayout.Toggle(_p.MuscleOn.Value, " Muscles: a resting tone, more where her pose tenses them");
            GUI.enabled = true;
            if (off == null && on != _p.MuscleOn.Value) _p.MuscleOn.Value = on;
            string sliderOff = off ?? (_p.MuscleOn.Value ? null : "muscles are off");
            string why = sliderOff == null ? null : "off: " + sliderOff;
            float fit = Slider("Fitness", _p.MuscleFitness.Value, 0f, 1f, "0.00", 0.3f, why);
            if (Mathf.Abs(fit - _p.MuscleFitness.Value) > 0.0001f) _p.MuscleFitness.Value = fit;
            float gain = Slider("Tension gain", _p.MuscleGain.Value, 0f, 2f, "0.00", 1f, why);
            if (Mathf.Abs(gain - _p.MuscleGain.Value) > 0.0001f) _p.MuscleGain.Value = gain;
            float st = Slider("Strength", _p.MuscleStrength.Value, 0f, 2f, "0.00", 1f, why);
            if (Mathf.Abs(st - _p.MuscleStrength.Value) > 0.0001f) _p.MuscleStrength.Value = st;
            float sh = Slider("Groove shade", _p.MuscleShade.Value, 0f, 1f, "0.00", 0.3f, why);
            if (Mathf.Abs(sh - _p.MuscleShade.Value) > 0.0001f) _p.MuscleShade.Value = sh;
            // 0.11.47 what tenses them: muscle lengths + loads (default), the old joint-angle tables, or the stronger of both
            GUI.enabled = sliderOff == null;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Driver", GUILayout.Width(170));
            int drv = GUILayout.Toolbar(Mathf.Clamp(_p.MuscleDriver.Value, 0, 2), MuscleDrivers);
            GUILayout.EndHorizontal();
            if (sliderOff == null && drv != _p.MuscleDriver.Value) _p.MuscleDriver.Value = drv;
            GUI.enabled = sliderOff == null;
            GUILayout.BeginHorizontal();
            bool core = GUILayout.Toggle(_p.MuscleCore.Value, " Core", GUILayout.Width(80));
            bool back = GUILayout.Toggle(_p.MuscleBack.Value, " Back", GUILayout.Width(80));
            bool legs = GUILayout.Toggle(_p.MuscleLegs.Value, " Legs", GUILayout.Width(80));
            bool arms = GUILayout.Toggle(_p.MuscleArms.Value, " Arms", GUILayout.Width(80));
            GUILayout.EndHorizontal();
            if (sliderOff == null)
            {
                if (core != _p.MuscleCore.Value) _p.MuscleCore.Value = core;
                if (back != _p.MuscleBack.Value) _p.MuscleBack.Value = back;
                if (legs != _p.MuscleLegs.Value) _p.MuscleLegs.Value = legs;
                if (arms != _p.MuscleArms.Value) _p.MuscleArms.Value = arms;
            }
            GUI.enabled = true;
            // 0.11.8 tensed muscles as geometry (GPU contact v2) and their firmness under a hand
            GUILayout.Label("Shape: tensed muscles bulge", _hint);
            var gpu = _body.Gpu;
            string gpuOff = off ?? (!_p.GpuContactOn.Value ? "GPU contact is off (Physics tab)" : gpu == null ? "shader bundle not loaded"
                           : !gpu.MuscleKernels ? "the shader bundle is older than this DLL (install dist/sfmorph_shaders)"
                           : gpu.MuscleVerts == 0 ? (gpu.FlexNote.Length > 0 ? gpu.FlexNote : "no muscle on her baked body meshes (yet)") : null);
            string flexOff = sliderOff ?? gpuOff;
            string fwhy = flexOff == null ? null : "off: " + flexOff;
            float fx = Slider("Flex", _p.MuscleFlexAmt.Value, 0f, 2f, "0.00", 1f, fwhy);
            if (Mathf.Abs(fx - _p.MuscleFlexAmt.Value) > 0.0001f) _p.MuscleFlexAmt.Value = fx;
            string awhy = fwhy ?? (_p.MuscleFlexAmt.Value <= 0f ? "off: Flex is 0" : null);
            float cm = Slider("Core", _p.MuscleCoreMm.Value, 0f, 20f, "0.0", 3f, awhy ?? (_p.MuscleCore.Value ? null : "off: Core is off"), " mm");
            if (Mathf.Abs(cm - _p.MuscleCoreMm.Value) > 0.0001f) _p.MuscleCoreMm.Value = cm;
            // 0.11.20 (user: the obliques bulge too much when only the abs should): relief, shade and bulge of the obliques against the core
            float ob = Slider("    obliques", _p.MuscleObliques.Value * 100f, 0f, 150f, "0", 60f, why ?? (_p.MuscleCore.Value ? null : "off: Core is off"), " %") / 100f;
            if (Mathf.Abs(ob - _p.MuscleObliques.Value) > 0.0001f) _p.MuscleObliques.Value = ob;
            float bm = Slider("Back", _p.MuscleBackMm.Value, 0f, 20f, "0.0", 4f, awhy ?? (_p.MuscleBack.Value ? null : "off: Back is off"), " mm");
            if (Mathf.Abs(bm - _p.MuscleBackMm.Value) > 0.0001f) _p.MuscleBackMm.Value = bm;
            float am = Slider("Arms", _p.MuscleArmsMm.Value, 0f, 20f, "0.0", 4f, awhy ?? (_p.MuscleArms.Value ? null : "off: Arms is off"), " mm");
            if (Mathf.Abs(am - _p.MuscleArmsMm.Value) > 0.0001f) _p.MuscleArmsMm.Value = am;
            float lm = Slider("Legs", _p.MuscleLegsMm.Value, 0f, 20f, "0.0", 4f, awhy ?? (_p.MuscleLegs.Value ? null : "off: Legs is off"), " mm");
            if (Mathf.Abs(lm - _p.MuscleLegsMm.Value) > 0.0001f) _p.MuscleLegsMm.Value = lm;
            // 0.11.37 (user: no broad upper torso setting here): the Body tab's Athletic upper body (bones: shoulders out, thicker arms and
            // neck, a wider ribcage) plus, with the muscles on, the lats / deltoids / traps / pecs filled out at rest (the V-taper)
            BroadUpperRow(c, sliderOff);
            float fm = Slider("Firmness", _p.MuscleFirmness.Value, 0f, 0.9f, "0.00", 0.5f, why);
            if (Mathf.Abs(fm - _p.MuscleFirmness.Value) > 0.0001f) _p.MuscleFirmness.Value = fm;
            if (off != null) GUILayout.Label("off: " + off, _off);
            else if (_p.MuscleOn.Value && _body.WrinkleStatus.Length > 0) GUILayout.Label(_body.WrinkleStatus, _body.WrinkleStatus.Contains("off:") ? _off : _hint);
            GUILayout.Label("Authored muscle relief (abs, obliques, serratus, ribcage; spine, lats, shoulder blades; glutes, quads, hamstrings, " +
                            "adductors, calves; deltoids, biceps, triceps, forearms; pecs, traps) in her skin's normal map. Fitness: the resting tone every muscle " +
                            "keeps. Tension gain: how much her pose adds: her spine bent forward or held leaning back tenses the abs, an arch or a " +
                            "bend over the back, a squat the glutes and thighs, a bent elbow the biceps, tiptoes the calves, arms overhead the " +
                            "serratus and lats, propping herself up the arms. Groove shade darkens the grooves in her skin colour. Surface detail " +
                            "only: a converted character's Fitness body slider (Characters tab) is the shape; this sits on top of either. Debug views " +
                            "are in the Debug tab. Speed is in the config file ([Muscles]). Shape: with GPU contact on, the sculpted muscles become " +
                            "geometry: soft abdominal blocks, oblique pads, serratus, pecs, traps, the erector columns, scapulae and lats, quads, hamstrings, calf heads, with soft valleys between them (mm = the " +
                            "area's highest belly at full activation, the rest in proportion; Fitness counts, so a high resting tone keeps some " +
                            "shape; mm x Flex eases toward 10 mm so a high setting stays muscle, not a lump). Obliques: the pads over her hip bones and the waist's sides as a share of Core (relief and bulge), so the abs can be raised alone. The relief above stays the fine detail. Firmness: a tensed muscle dents less under a hand and the physics hands meet more resistance there. Flex test: " +
                            "each area at full (at least 3 mm) for 2 s over whatever she is doing, whatever her pose's tension; the log says how far " +
                            "and on how many vertices each one went.", _hint);
        }

        void BroadUpperRow(Character c, string sliderOff)
        {
            Detail d = null;
            foreach (var x in Detail.All) if (x.Id == "upperBuild") { d = x; break; }
            if (d == null || c == null || c.Look == null) return;
            float v;
            if (!c.Look.Regions.TryGetValue(d.Id, out v)) v = d.Neutral;
            string off;
            c.Off.TryGetValue(d.Id, out off);
            float nv = Slider("Broad upper body", v, d.Min, d.Max, "0.00", d.Neutral, off == null ? null : "off: " + off);
            if (nv != v)
            {
                if (Mathf.Abs(nv - d.Neutral) < 0.005f) c.Look.Regions.Remove(d.Id); else c.Look.Regions[d.Id] = nv;
                _body.Touch(c);
            }
            GUILayout.Label("    Broader shoulders and back, thicker arms and neck (the Body tab's Athletic upper body, saved with her look); with muscles on, " +
                            "her lats, deltoids, traps and upper chest stay filled out at rest for the V-taper.", _hint);
        }

        // 0.11.31 chubby: soft fat by area on the GPU skin (like the muscle shape, the other way)
        void ChubbySection(Character c)
        {
            GUILayout.Label("Chubby", _bold);
            var gpu = _body.Gpu;
            string off = !_p.GpuContactOn.Value ? "GPU contact is off (Physics tab)" : gpu == null ? "shader bundle not loaded"
                       : !gpu.FoldKernels ? "the shader bundle is older than this DLL (install dist/sfmorph_shaders)"
                       : gpu.FatVerts == 0 ? "no chubby areas on her baked body meshes (yet)" : null;
            string why = off == null ? null : "off: " + off;
            float ch = Slider("Chubby", _p.Chubby.Value * 100f, -100f, 200f, "0", 0f, why, " %") / 100f;
            if (Mathf.Abs(ch - _p.Chubby.Value) > 0.0001f) _p.Chubby.Value = ch;
            string awhy = why ?? (_p.Chubby.Value != 0f ? null : "off: Chubby is 0");
            for (int i = 0; i < _p.ChubbyArea.Length; i++)
            {
                float a = Slider("    " + Plugin.ChubbyNames[i].ToLowerInvariant(), _p.ChubbyArea[i].Value, 0f, 40f, "0", (float)_p.ChubbyArea[i].DefaultValue, awhy, " mm");
                if (Mathf.Abs(a - _p.ChubbyArea[i].Value) > 0.001f) _p.ChubbyArea[i].Value = a;
            }
            GUILayout.Label("Glutes: fuller and also softer in her physics, like the breasts (they wobble more, give further under a hand or his body); " +
                            "belly and thighs too, with their own fat.", _hint);
            GUILayout.Label("Soft fat where she puts it on: belly (the pooch under the navel, a softer upper belly), waist (love handles), back " +
                            "(the bra line, the lower back), glutes, thighs (saddlebags, inner thighs), arms (the backs of the upper arms), calves, face (fuller cheeks, a double chin, a softer neck). mm = " +
                            "the area's fullest spot at 100 %. Fat hides the muscle shape under it, dents deeper under a hand, ripples more, and makes " +
                            "the belly and waist rolls and the folds beside her joints fuller. Her clothes don't follow it.", _hint);
        }

        bool Card(Texture2D thumb, string name, bool on, float size)
        {
            GUILayout.BeginVertical(GUILayout.Width(size));
            bool hit = thumb != null ? GUILayout.Toggle(on, thumb, GUI.skin.button, GUILayout.Width(size), GUILayout.Height(size))
                                     : GUILayout.Toggle(on, name, GUI.skin.button, GUILayout.Width(size), GUILayout.Height(size));
            GUILayout.Label((on ? "> " : "") + name, on ? _bold : GUI.skin.label, GUILayout.Width(size));
            GUILayout.EndVertical();
            return hit && !on;
        }

        // ---- controls ----

        void Filter(ref string filter, int changed)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Filter", GUILayout.Width(45));
            filter = GUILayout.TextField(filter, GUILayout.MinWidth(150));
            if (GUILayout.Button("x", GUILayout.Width(24))) filter = "";
            GUI.enabled = changed > 0 || _changedOnly;
            _changedOnly = GUILayout.Toggle(_changedOnly, " changed only (" + changed + ")", GUILayout.Width(170));
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        // 0.11.47: a 0..100 % row for a 0..1 Delta Mush setting (default 1)
        void MushRow(string label, BepInEx.Configuration.ConfigEntry<float> e)
        {
            float v = Slider(label, e.Value * 100f, 0f, 100f, "0", 100f, null, " %") / 100f;
            if (Mathf.Abs(v - e.Value) > 0.0001f) e.Value = v;
        }

        // Label, slider, value and a reset button; greyed with the reason when it can't apply.
        float Slider(string label, float v, float min, float max, string fmt, float neutral, string off = null, string unit = "")
        {
            GUILayout.BeginHorizontal();
            GUI.enabled = off == null;
            GUILayout.Label(label, GUILayout.Width(170));
            float nv = GUILayout.HorizontalSlider(v, min, max, GUILayout.MinWidth(120));
            GUILayout.Label(v.ToString(fmt, CultureInfo.InvariantCulture) + unit, GUILayout.Width(64));
            GUI.enabled = off == null && Mathf.Abs(v - neutral) > 0.0001f;
            if (GUILayout.Button("Reset", GUILayout.Width(52))) nv = neutral;
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (off != null) { GUILayout.Label("    " + off, _off); return v; }
            return nv;
        }
    }
}
