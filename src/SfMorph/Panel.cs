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

        static readonly string[] Tabs = { "Body", "Bones", "Presets", "Characters", "Physics", "VR", "Reactions" };   // appended: the saved Tab indices 0-3 stay valid

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
            }
            float scale = Mathf.Clamp(_p.UiScale.Value, 0.75f, 2.5f);
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            var before = _rect;
            _rect = GUILayout.Window(0x5F4D4F, _rect, Window, "SF Body Morphs " + Plugin.Version, GUILayout.Width(_rect.width), GUILayout.Height(_rect.height));
            float sw = Screen.width / scale, sh = Screen.height / scale;
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, sw - _rect.width));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, sh - _rect.height));
            if (_rect.x != before.x || _rect.y != before.y)
                _p.PanelRect.Value = string.Format(CultureInfo.InvariantCulture, "{0:0},{1:0},{2:0},{3:0}", _rect.x, _rect.y, _rect.width, _rect.height);
            GUI.matrix = old;
        }

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
            if (pick != null) _sel = pick;
            GUILayout.EndHorizontal();
            if (_sel == null)
            {
                GUILayout.Label("Load a scene with a woman in it. Characters are rescanned every 2 seconds.", _hint);
                GUI.DragWindow();
                return;
            }

            GUILayout.BeginHorizontal();
            int tab = GUILayout.Toolbar(Mathf.Clamp(_p.Tab.Value, 0, Tabs.Length - 1), Tabs);
            if (tab != _p.Tab.Value) { _p.Tab.Value = tab; _scroll = Vector2.zero; }
            GUI.enabled = !_sel.Look.Empty;
            if (GUILayout.Button("Reset " + _sel.Key, GUILayout.Width(150))) { _sel.Look.Clear(); _body.Touch(_sel); }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll);
            switch (tab)
            {
                case 0: BodyTab(_sel); break;
                case 1: BoneTab(_sel); break;
                case 2: PresetTab(_sel); break;
                case 3: CharacterTab(_sel); break;
                case 4: PhysicsTab(_sel); break;
                case 5: VrTab(_sel); break;
                default: ReactionsTab(_sel); break;
            }
            GUILayout.EndScrollView();
            GUI.DragWindow();
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
            if (pick != null && c.Look.Morph != pick) { c.Look.Morph = pick; c.Look.MorphFace = 1f; c.Look.MorphBody = 1f; _body.Touch(c); }
            if (all.Count == 0)
                GUILayout.Label("off: no converted characters yet. Export her (Presets tab) and a VaM character (SfExport plugin), then run tools/pipeline/run.ps1.", _off);
            if (GUILayout.Button("Rescan the folder", GUILayout.Width(160))) MorphSet.Rescan();

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
            PushesSection(c);
            GUILayout.Space(10);
            WrinklesSection(c);
        }

        void VrTab(Character c)
        {
            EmbodySection(c);
            GUILayout.Space(10);
            HandsSection(c);
            GUILayout.Space(10);
            ContactsSection(c);
        }

        void EmbodySection(Character c)
        {
            GUILayout.Label("Embody", _bold);
            string off = !c.Female || c.IsPlayer ? "only with the game's woman in the scene" : null;
            GUI.enabled = off == null;
            bool on = GUILayout.Toggle(_p.EmbodyOn.Value, " Embody him: see through his eyes, his hands on your controllers");
            GUI.enabled = true;
            if (off == null && on != _p.EmbodyOn.Value) _p.EmbodyOn.Value = on;
            if (off != null) { GUILayout.Label("off: " + off, _off); return; }
            GUI.enabled = _p.EmbodyOn.Value;
            int partner = GUILayout.Toolbar(_p.EmbodyPartner.Value, Partners);
            if (partner != _p.EmbodyPartner.Value) _p.EmbodyPartner.Value = partner;
            GUILayout.Label("View", _hint);
            GUILayout.BeginHorizontal();
            int view = GUILayout.Toolbar(_p.EmbodyView.Value, ViewModes);
            if (view != _p.EmbodyView.Value) _p.EmbodyView.Value = view;
            if (GUILayout.Button("Re-snap", GUILayout.Width(80))) _body.Embody.SnapView();
            GUILayout.EndHorizontal();
            bool hide = GUILayout.Toggle(_p.EmbodyHideHead.Value, " Hide his head");
            if (hide != _p.EmbodyHideHead.Value) _p.EmbodyHideHead.Value = hide;
            GUILayout.Label("His hands follow", _hint);
            int hands = GUILayout.Toolbar(_p.EmbodyHands.Value, HandModes);
            if (hands != _p.EmbodyHands.Value) _p.EmbodyHands.Value = hands;
            GUI.enabled = true;
            if (_p.EmbodyOn.Value && _body.Embody.Status.Length > 0) GUILayout.Label(_body.Embody.Status, _body.Embody.Status.StartsWith("off") ? _off : _hint);
            GUILayout.Label("The game's own POV camera can end up elsewhere; 'Follow his eyes' keeps your view at his eyes (position only, " +
                            "you turn your own head). Hand offsets per controller: [Embody] in sfmodvr.sfmorph.cfg.", _hint);
        }

        void MorphControls(Character c)
        {
            GUILayout.Space(6);
            GUILayout.Label(c.Look.Morph, _bold);
            var set = MorphSet.Find(c.Look.Morph);
            bool two = set != null && set.TwoFrames;   // decided once, before any control: the same controls in the Layout pass and the click pass
            float fw = Slider("Face", c.Look.MorphFace * 100f, 0f, 100f, "0", 100f, null, " %") / 100f;
            if (Mathf.Abs(fw - c.Look.MorphFace) > 0.0001f) { c.Look.MorphFace = fw; _body.Touch(c); }
            float bw = Slider("Body", c.Look.MorphBody * 100f, 0f, 100f, "0", 100f, two ? null : "off: this character has a single shape (Face drives all of it)", " %") / 100f;
            if (two && Mathf.Abs(bw - c.Look.MorphBody) > 0.0001f) { c.Look.MorphBody = bw; _body.Touch(c); }
            GUILayout.Label("Face: the character's face and head. Body: her figure (lower it if the body looks too heavy in her animations).", _hint);
            bool j = GUILayout.Toggle(c.Look.MorphJoints, " Move her joints to the new shape (limbs bend in the right place)");
            if (j != c.Look.MorphJoints) { c.Look.MorphJoints = j; _body.Touch(c); }
            GUILayout.BeginHorizontal();
            bool sk = GUILayout.Toggle(c.Look.MorphSkin, " Face skin texture", GUILayout.Width(200));
            if (sk != c.Look.MorphSkin) { c.Look.MorphSkin = sk; _body.Touch(c); }
            bool skb = GUILayout.Toggle(c.Look.MorphSkinBody, " Body skin texture");
            if (skb != c.Look.MorphSkinBody) { c.Look.MorphSkinBody = skb; _body.Touch(c); }
            GUILayout.EndHorizontal();
            // presets, applied after the row so the Layout and click passes draw the same controls
            GUILayout.BeginHorizontal();
            bool faceOnly = GUILayout.Button("Face only (body stays hers)");
            bool full = GUILayout.Button("Full character");
            GUILayout.EndHorizontal();
            if (faceOnly) { c.Look.MorphFace = 1f; c.Look.MorphBody = 0f; c.Look.MorphSkin = true; c.Look.MorphSkinBody = false; _body.Touch(c); }
            else if (full) { c.Look.MorphFace = 1f; c.Look.MorphBody = 1f; c.Look.MorphSkin = true; c.Look.MorphSkinBody = true; _body.Touch(c); }
            GUILayout.Label("Facial likeness only: Body 0 %, Face 100 %, face skin on, body skin off.", _hint);
            if (!_p.Apply.Value) GUILayout.Label("off: 'Apply changes' is off", _off);
            else if (c.Morph.Status.Length > 0) GUILayout.Label(c.Morph.Status, c.Morph.Status.StartsWith("off") ? _off : _hint);
            GUILayout.Label("Clothes follow the skin by its nearest vertex and are pushed out where the new shape would poke through. " +
                            "Her height stays the game's (the VaM character is scaled to her eye height), so scenes still line up.", _hint);
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
            if (handsOff != null) { GUI.enabled = true; GUILayout.Label("    off: " + handsOff, _off); }
            float fr = Slider("Grip friction", _p.HandsFriction.Value * 100f, 0f, 100f, "0", 80f, hitOff == null ? null : "off: " + hitOff, " %") / 100f;
            if (hitOff == null && Mathf.Abs(fr - _p.HandsFriction.Value) > 0.0001f) _p.HandsFriction.Value = fr;
            GUI.enabled = true;
            if (handsOn && PhysicsHands.Status.Length > 0) GUILayout.Label(PhysicsHands.Status, PhysicsHands.Status.StartsWith("off") ? _off : _hint);
            GUILayout.Label("Grip curls the fingers. Friction: a resting hand is carried along when she moves. " +
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
            float lp = Slider("Push her limbs", push * 100f, 0f, 100f, "0", 50f, off == null ? null : "off: " + off, " %") / 100f;
            if (off == null && Mathf.Abs(lp - _p.HandsLimbPush.Value) > 0.0001f) _p.HandsLimbPush.Value = lp;
            string byOff = off ?? (push > 0f ? null : "push is 0");
            bool byTouch = _p.HandsPushByTouch.Value;
            string partnerOff = byOff ?? (byTouch ? null : "other hands don't push");
            GUI.enabled = byOff == null;
            bool touch = GUILayout.Toggle(byTouch, " Other hands push too (your hand models, the partner you embody), by how deep they are");
            GUI.enabled = partnerOff == null;
            bool partner = GUILayout.Toggle(_p.HandsPushByPartner.Value, "   ...and the partner's animated hands (his animations often hold her: off keeps her in his grip)");
            GUI.enabled = true;
            if (byOff == null && touch != _p.HandsPushByTouch.Value) _p.HandsPushByTouch.Value = touch;
            if (partnerOff == null && partner != _p.HandsPushByPartner.Value) _p.HandsPushByPartner.Value = partner;
            if (byOff != null) GUILayout.Label("    off: " + byOff, _off);
            if (off == null && HerBody.Status.Length > 0) GUILayout.Label(HerBody.Status, HerBody.Status.StartsWith("off") ? _off : _hint);
            GUILayout.Label("Push: a hand that presses into an arm, leg, her head or torso moves it aside and the pose springs back (never past 30/20/15/10 degrees). " +
                            "Physics hands push by how hard they press; other hands by how deep they are in her.", _hint);
        }

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
            bool planted = GUILayout.Toggle(_p.OverlapPlanted.Value, " Planted hands and feet stay where the animation puts them");
            GUI.enabled = true;
            if (sub == null && planted != _p.OverlapPlanted.Value) _p.OverlapPlanted.Value = planted;
            if (off == null && _body.Overlap.Status.Length > 0) GUILayout.Label(_body.Overlap.Status, _body.Overlap.Status.StartsWith("off") ? _off : _hint);
            GUILayout.Label("Her elbows, wrists, knees, head and spine trail fast animation a little and settle (follow-through). " +
                            "It fades out where a hand or her other leg touches her, so contacts don't slide. " +
                            "Ripple: motion travels down the chains (a fast turn of the torso swings the head and arms a little later). " +
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
            GUILayout.BeginHorizontal();
            bool gpu = GUILayout.Toggle(_p.GpuContactOn.Value, " Per-vertex contact on the GPU");
            if (sliderOff == null && gpu != _p.GpuContactOn.Value) _p.GpuContactOn.Value = gpu;
            GUI.enabled = sliderOff == null && _p.GpuContactOn.Value && _body.Gpu != null;
            if (GUILayout.Button("Smoke test (3 s)", GUILayout.Width(140))) _body.Gpu.Smoke(3f, 0.01f);
            GUI.enabled = sliderOff == null;
            GUILayout.EndHorizontal();
            if (sliderOff == null && _p.GpuContactOn.Value && _body.GpuStatus.Length > 0) GUILayout.Label(_body.GpuStatus, _body.GpuStatus.Contains("off:") ? _off : _hint);
            GUILayout.Label("Breast physics colliders (hands push the game's own breast physics)", _hint);
            int bc = GUILayout.Toolbar(Mathf.Clamp(_p.BreastColliders.Value, 0, 2), BreastModes);
            if (sliderOff == null && bc != _p.BreastColliders.Value) _p.BreastColliders.Value = bc;
            bool show = GUILayout.Toggle(_p.ShowSoftPoints.Value, " Show soft points and contact capsules (debug)");
            if (sliderOff == null && show != _p.ShowSoftPoints.Value) _p.ShowSoftPoints.Value = show;
            GUI.enabled = true;
            if (off != null) GUILayout.Label("off: " + off, _off);
            else
            {
                if (c.Soft.Status.Length > 0) GUILayout.Label(c.Soft.Status, c.Soft.Status.StartsWith("off") ? _off : _hint);
                if (c.Soft.Contacts.Length > 0) GUILayout.Label(c.Soft.Contacts, _hint);
            }
            GUILayout.Label("Soft points under her skin (springs, like VaM's soft physics) are pushed by your VR hands, the partner's hands and fingers, " +
                            "her forearms and hands, and her other thigh. Softness: how far the skin gives. Springiness: how much the zones lag " +
                            "and settle as she moves. Works with or without a character shape. Which hands count: VR tab, Contacts.", _hint);
        }

        static readonly string[] WrinkleDebugs = { "Off", "Activation", "Axis" };

        void WrinklesSection(Character c)
        {
            GUILayout.Label("Skin wrinkles", _bold);
            string off = !_p.Apply.Value ? "'Apply changes' is off"
                       : !c.Female || c.IsPlayer ? "only for the game's woman" : null;
            GUI.enabled = off == null;
            bool on = GUILayout.Toggle(_p.WrinkOn.Value, " Wrinkles: folds in her skin where it compresses (expressions, bending)");
            GUI.enabled = true;
            if (off == null && on != _p.WrinkOn.Value) _p.WrinkOn.Value = on;
            string sliderOff = off ?? (_p.WrinkOn.Value ? null : "wrinkles are off");
            string why = sliderOff == null ? null : "off: " + sliderOff;
            float st = Slider("Strength", _p.WrinkStrength.Value, 0f, 2f, "0.00", 1f, why);
            if (Mathf.Abs(st - _p.WrinkStrength.Value) > 0.0001f) _p.WrinkStrength.Value = st;
            float th = Slider("Threshold", _p.WrinkThreshold.Value, 0.01f, 0.06f, "0.000", 0.025f, why);
            if (Mathf.Abs(th - _p.WrinkThreshold.Value) > 0.00001f) _p.WrinkThreshold.Value = th;
            GUI.enabled = sliderOff == null;
            GUILayout.BeginHorizontal();
            bool face = GUILayout.Toggle(_p.WrinkFace.Value, " Face", GUILayout.Width(90));
            bool neck = GUILayout.Toggle(_p.WrinkNeck.Value, " Neck", GUILayout.Width(90));
            bool body = GUILayout.Toggle(_p.WrinkBody.Value, " Body (slower to build)");
            GUILayout.EndHorizontal();
            if (sliderOff == null)
            {
                if (face != _p.WrinkFace.Value) _p.WrinkFace.Value = face;
                if (neck != _p.WrinkNeck.Value) _p.WrinkNeck.Value = neck;
                if (body != _p.WrinkBody.Value) _p.WrinkBody.Value = body;
            }
            GUILayout.Label("Debug view", _hint);
            int dbg = GUILayout.Toolbar(Mathf.Clamp(_p.WrinkDebug.Value, 0, 2), WrinkleDebugs);
            if (sliderOff == null && dbg != _p.WrinkDebug.Value) _p.WrinkDebug.Value = dbg;
            GUI.enabled = sliderOff == null && _body.Wrink != null;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Recapture", GUILayout.Width(110))) _body.RequestWrinkleRecapture();
            GUILayout.Label("re-reads her resting skin (after changing a body slider or shape)", _hint);
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            if (off != null) GUILayout.Label("off: " + off, _off);
            else if (_p.WrinkOn.Value && _body.WrinkleStatus.Length > 0) GUILayout.Label(_body.WrinkleStatus, _body.WrinkleStatus.Contains("off:") ? _off : _hint);
            GUILayout.Label("Where her skin compresses (a smile, a frown, bending) fine folds appear in the skin's normal map, computed on the GPU. " +
                            "Threshold: how much it has to compress first. Debug: Activation tints compressed skin red, Axis shows the fold direction. " +
                            "Spacing, speed and texture size are in the config file ([Wrinkles]).", _hint);
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
