using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace SfMorph
{
    // VaM-style body editing for Sex Formula's female characters: the game's
    // own blendshapes (morphs) as additive sliders, body-part sizes through bone
    // scaling, per-character autosave and presets. Independent of SfNr (own
    // GUID, config, panel and key), so both load side by side.
    [BepInPlugin(Guid, "SF Body Morphs", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "sfmodvr.sfmorph";
        public const string Version = "0.7.1";

        internal static BepInEx.Logging.ManualLogSource Log;
        internal ConfigEntry<KeyCode> PanelKey;
        internal ConfigEntry<bool> KeysUnfocused, Apply, Physics, ShowSoftPoints, EmbodyOn, EmbodyHideHead, HandsOn, HandsCollide;
        internal ConfigEntry<float> UiScale, Softness, Springiness;
        internal ConfigEntry<int> EmbodyHands, EmbodyView, EmbodyPartner, HandsMesh;
        internal ConfigEntry<Vector3> LeftHandPos, LeftHandRot, RightHandPos, RightHandRot;
        internal ConfigEntry<string> PanelRect;
        internal ConfigEntry<int> Tab;

        void Awake()
        {
            Log = Logger;
            PanelKey = Config.Bind("Interface", "PanelKey", KeyCode.F8, "Body morphs panel in the desktop window.");
            KeysUnfocused = Config.Bind("Interface", "KeysWhenUnfocused", true, "The key also works while another window (Virtual Desktop, SteamVR) has focus.");
            UiScale = Config.Bind("Interface", "PanelScale", 1.25f, new ConfigDescription("Size of the panel's text and controls.", new AcceptableValueRange<float>(0.75f, 2.5f)));
            PanelRect = Config.Bind("Interface", "PanelRect", "", "Panel position and size (x,y,width,height), saved when you move or resize it.");
            Tab = Config.Bind("Interface", "Tab", 0, "Last panel tab.");
            Apply = Config.Bind("General", "Apply", true, "Apply the body changes (off = the game's own look, for comparing).");
            Physics = Config.Bind("Physics", "SoftZones", true, "Hands and limbs press into her glutes, thighs, belly and breasts (helper bones driven by the game's DynamicBone).");
            Softness = Config.Bind("Physics", "Softness", 0.5f, new ConfigDescription("How much the soft zones give (0 firm, 1 soft).", new AcceptableValueRange<float>(0f, 1f)));
            Springiness = Config.Bind("Physics", "Springiness", 0.3f, new ConfigDescription("Secondary motion of the soft zones from her own movement (0 none, 1 a lot).", new AcceptableValueRange<float>(0f, 1f)));
            ShowSoftPoints = Config.Bind("Physics", "ShowSoftPoints", false, "Small markers on the soft points (green at rest, red touched) and the contact capsules, to see what touches what.");
            EmbodyOn = Config.Bind("Embody", "On", false, "See through the partner's eyes (VR), his head hidden, his hands on your controllers.");
            EmbodyHands = Config.Bind("Embody", "Hands", 3, new ConfigDescription("His hands follow the controllers: 0 none, 1 left, 2 right, 3 both.", new AcceptableValueRange<int>(0, 3)));
            EmbodyHideHead = Config.Bind("Embody", "HideHead", true, "Hide his head so it doesn't block the view.");
            EmbodyView = Config.Bind("Embody", "View", 2, new ConfigDescription("0 the game's camera, 1 snap to his eyes once, 2 follow his eyes.", new AcceptableValueRange<int>(0, 2)));
            EmbodyPartner = Config.Bind("Embody", "Partner", 0, new ConfigDescription("0 the player, 1 the other man (badGuy).", new AcceptableValueRange<int>(0, 1)));
            HandsOn = Config.Bind("Hands", "PhysicsHands", false, "Replace the VR controller models with physics-driven hands that collide with her and press her soft zones.");
            HandsMesh = Config.Bind("Hands", "Mesh", 0, new ConfigDescription("0 the game's own (male) hands, 1 simple shapes.", new AcceptableValueRange<int>(0, 1)));
            HandsCollide = Config.Bind("Hands", "CollideWithHer", true, "The hands stop at her body (after the skin gives) instead of passing through.");
            LeftHandPos = Config.Bind("Embody", "LeftHandOffset", new Vector3(-0.0325f, -0.0379f, -0.1012f), "Controller -> his left hand position offset (m, controller space). Quest Touch default.");
            LeftHandRot = Config.Bind("Embody", "LeftHandRotation", Vector3.zero, "Controller -> his left hand rotation (degrees), added to the automatic alignment (fingers along the controller, thumb up).");
            RightHandPos = Config.Bind("Embody", "RightHandOffset", new Vector3(0.0325f, -0.0379f, -0.1012f), "Controller -> his right hand position offset (m).");
            RightHandRot = Config.Bind("Embody", "RightHandRotation", Vector3.zero, "Controller -> his right hand rotation (degrees), added to the automatic alignment.");
            Logger.LogInfo("SF Body Morphs " + Version + ": panel on " + PanelKey.Value + ", looks in " + Store.Dir);
            // The game destroys plugin components; a hidden object of our own keeps running.
            var go = new GameObject("SfMorphRunner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<Runner>().Init(this);
        }
    }

    internal class Runner : MonoBehaviour
    {
        Plugin _p;
        Body _body;
        Panel _panel;
        bool _broken, _inLoop;
        float _nextLoopCheck;

        public void Init(Plugin p)
        {
            _p = p;
            _body = new Body(p);
            _panel = new Panel(p, _body);
        }

        void Update()
        {
            if (_broken) return;
            try
            {
                if (Keys.Pressed(_p.PanelKey.Value, _p.KeysUnfocused.Value)) _panel.Visible = !_panel.Visible;
                _body.Tick();
                if (Time.unscaledTime >= _nextLoopCheck)
                {
                    _nextLoopCheck = Time.unscaledTime + 2f;
                    bool was = _inLoop;
                    _inLoop = ApplyStep.Ensure(Apply);
                    if (_inLoop != was)
                        Plugin.Log.LogInfo(_inLoop ? "Body changes apply after the game's animation, before its physics and IK"
                                                   : "Couldn't join Unity's frame loop; body changes apply in LateUpdate (breast physics may jitter)");
                }
            }
            catch (Exception e) { Fail(e); }
        }

        // Physics hands follow the controllers with velocities, so they move in the physics step.
        void FixedUpdate()
        {
            if (_broken) return;
            try { PhysicsHands.FixedStep(); }
            catch (Exception e) { Plugin.Log.LogError("Physics hands failed, turned off: " + e); _p.HandsOn.Value = false; try { PhysicsHands.Release(); } catch { } }
        }

        // Fallback only: in LateUpdate the game's physics may already have run for this frame.
        void LateUpdate() { if (!_inLoop) Apply(); }

        void Apply()
        {
            if (_broken) return;
            try { _body.ApplyAll(); }
            catch (Exception e) { Fail(e); }
        }

        void OnGUI()
        {
            if (_broken) return;
            try { _panel.Draw(); }
            catch (Exception e) { Fail(e); }
        }

        void Fail(Exception e)
        {
            _broken = true;
            Plugin.Log.LogError("SF Body Morphs stopped after an error (restart the game to retry): " + e);
            try { _body.RestoreAll(); } catch { }
        }
    }

    // The body changes must land after the game's animation (which may rewrite bones every
    // frame) and before its LateUpdate scripts: DynamicBone breast/hair physics and FinalIK
    // simulate on the bones they find there. Applied after them (0.2.x), the physics ran on the
    // unchanged body and was then resized under it, a mismatch every frame: jitter.
    // Unity's frame loop runs animation (Director/constraints) in PreLateUpdate, then the
    // scripts' LateUpdate; this step goes right before ScriptRunBehaviourLateUpdate.
    internal struct ApplyStep
    {
        public static bool Ensure(PlayerLoopSystem.UpdateFunction fn)
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var subs = root.subSystemList;
            if (subs == null) return false;
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type != typeof(PreLateUpdate) || subs[i].subSystemList == null) continue;
                var list = new List<PlayerLoopSystem>(subs[i].subSystemList);
                if (list.Exists(s => s.type == typeof(ApplyStep))) return true;
                int at = list.FindIndex(s => s.type == typeof(PreLateUpdate.ScriptRunBehaviourLateUpdate));
                if (at < 0) return false;
                list.Insert(at, new PlayerLoopSystem { type = typeof(ApplyStep), updateDelegate = fn });
                subs[i].subSystemList = list.ToArray();
                root.subSystemList = subs;
                PlayerLoop.SetPlayerLoop(root);
                return true;
            }
            return false;
        }
    }

    // Key presses that also work while the game window isn't focused (in VR the
    // focus is usually on Virtual Desktop or SteamVR).
    internal static class Keys
    {
        [DllImport("user32")] static extern short GetAsyncKeyState(int vk);
        static bool _was;

        public static bool Pressed(KeyCode key, bool unfocused)
        {
            if (key == KeyCode.None) return false;
            int vk = key >= KeyCode.F1 && key <= KeyCode.F15 ? 0x70 + (key - KeyCode.F1)
                   : key >= KeyCode.A && key <= KeyCode.Z ? 0x41 + (key - KeyCode.A)
                   : key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9 ? 0x30 + (key - KeyCode.Alpha0) : 0;
            if (!unfocused || vk == 0) return Input.GetKeyDown(key);
            bool now = (GetAsyncKeyState(vk) & 0x8000) != 0;
            bool pressed = now && !_was;
            _was = now;
            return pressed;
        }
    }
}
