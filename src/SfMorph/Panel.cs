using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SfMorph
{
    // Desktop-window panel (F8): pick a woman, then Body (part sizes), Morphs
    // (the game's blendshapes), Bones (any skin bone) and Presets.
    internal class Panel
    {
        readonly Plugin _p;
        readonly Body _body;
        public bool Visible;
        Rect _rect = new Rect(700, 40, 560, 720);
        Vector2 _scroll;
        Character _sel;
        string _shapeFilter = "", _boneFilter = "", _presetName = "";
        bool _changedOnly;
        string _bone;
        string _message = "";
        List<string> _presets;
        GUIStyle _hint, _bold, _off;
        const int MaxRows = 80; // IMGUI gets slow with hundreds of sliders; the filter narrows it

        static readonly string[] Tabs = { "Body", "Morphs", "Bones", "Presets" };

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
                case 1: MorphTab(_sel); break;
                case 2: BoneTab(_sel); break;
                default: PresetTab(_sel); break;
            }
            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        // ---- tabs ----

        void BodyTab(Character c)
        {
            GUILayout.Label("Size of each body part, on top of the game's own shape. Bones come from the game's own rig " +
                            "where it names them, otherwise from bone names; a part with no bone is greyed out (use the Bones tab).", _hint);
            foreach (var reg in Region.All)
            {
                var bones = c.RegionBones[reg.Id];
                float v;
                if (!c.Look.Regions.TryGetValue(reg.Id, out v)) v = 1f;
                string off = bones.Count == 0 ? "off: no matching bone in this rig" : null;
                float nv = Slider(reg.Label, v, reg.Min, reg.Max, "0.00", off);
                if (off == null)
                {
                    var names = "";
                    foreach (var b in bones) names += (names.Length > 0 ? ", " : "") + b.name;
                    GUILayout.Label("    " + names + (c.FromGame.Contains(reg.Id) ? "  (the game's own bone)" : "  (found by name)"), _hint);
                }
                if (nv != v)
                {
                    if (Mathf.Abs(nv - 1f) < 0.005f) c.Look.Regions.Remove(reg.Id); else c.Look.Regions[reg.Id] = nv;
                    _body.Touch(c);
                }
            }
            GUILayout.Label("Big changes to hips, thighs or arms move where hands and hips meet in the game's animations. " +
                            "Head size also shifts the game's eyelid tracking a little.", _hint);
        }

        void MorphTab(Character c)
        {
            if (c.ShapeNames.Count == 0)
            {
                GUILayout.Label("off: this character's meshes have no blendshapes (morphs); use the Body and Bones tabs.", _off);
                return;
            }
            GUILayout.Label("The game's blendshapes. The slider adds to the game's weight (100 = the full shape), so face " +
                            "expressions and lip sync keep working; past 100 or below 0 exaggerates or inverts a shape, like VaM.", _hint);
            Filter(ref _shapeFilter, c.Look.Shapes.Count);
            int shown = 0, matched = 0;
            foreach (var name in c.ShapeNames)
            {
                float v;
                bool set = c.Look.Shapes.TryGetValue(name, out v);
                if (_changedOnly && !set) continue;
                if (_shapeFilter.Length > 0 && name.IndexOf(_shapeFilter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (++matched > MaxRows) continue;
                shown++;
                float game = _body.GameWeight(c, name);
                float nv = Slider(name, v, -100f, 200f, "0", null, "game " + game.ToString("0"));
                if (nv != v)
                {
                    if (Mathf.Abs(nv) < 0.5f) c.Look.Shapes.Remove(name); else c.Look.Shapes[name] = Mathf.Round(nv);
                    _body.Touch(c);
                }
            }
            if (matched > shown) GUILayout.Label((matched - shown) + " more: type in the filter to narrow the list.", _hint);
        }

        void BoneTab(Character c)
        {
            GUILayout.Label("Scale any bone of the skin (its children scale with it). For VaM-style part sizes use the Body tab.", _hint);
            Transform sel;
            if (_bone != null && c.Bones.TryGetValue(_bone, out sel))
            {
                Vector3 v;
                if (!c.Look.Bones.TryGetValue(_bone, out v)) v = Vector3.one;
                GUILayout.Label(_bone, _bold);
                float u = Slider("All", (v.x + v.y + v.z) / 3f, 0.3f, 3f, "0.00");
                var nv = Mathf.Abs(u - (v.x + v.y + v.z) / 3f) > 0.0001f ? new Vector3(u, u, u) : v;
                nv.x = Slider("X", nv.x, 0.3f, 3f, "0.00");
                nv.y = Slider("Y", nv.y, 0.3f, 3f, "0.00");
                nv.z = Slider("Z", nv.z, 0.3f, 3f, "0.00");
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
            if (_presets == null) _presets = Store.Presets();
            if (_presets.Count == 0) GUILayout.Label("No presets yet.", _hint);
            foreach (var name in _presets)
                if (GUILayout.Button("Load '" + name + "' onto " + c.Key))
                {
                    var look = new Look();
                    if (Store.LoadPreset(name, look)) { c.Look.CopyFrom(look); _body.Touch(c); _message = "Loaded '" + name + "' onto " + c.Key + "."; }
                }
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
        float Slider(string label, float v, float min, float max, string fmt, string off = null, string extra = null)
        {
            float neutral = min < 0f ? 0f : 1f;
            GUILayout.BeginHorizontal();
            GUI.enabled = off == null;
            GUILayout.Label(label, GUILayout.Width(170));
            float nv = GUILayout.HorizontalSlider(v, min, max, GUILayout.MinWidth(120));
            GUILayout.Label(v.ToString(fmt, CultureInfo.InvariantCulture), GUILayout.Width(44));
            if (extra != null) GUILayout.Label(extra, _hint, GUILayout.Width(70));
            GUI.enabled = off == null && Mathf.Abs(v - neutral) > 0.0001f;
            if (GUILayout.Button("Reset", GUILayout.Width(52))) nv = neutral;
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (off != null) { GUILayout.Label("    " + off, _off); return v; }
            return nv;
        }
    }
}
