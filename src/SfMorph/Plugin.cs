using System;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

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
        public const string Version = "0.2.1";

        internal static BepInEx.Logging.ManualLogSource Log;
        internal ConfigEntry<KeyCode> PanelKey;
        internal ConfigEntry<bool> KeysUnfocused, Apply;
        internal ConfigEntry<float> UiScale;
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
            Logger.LogInfo("SF Body Morphs " + Version + ": panel on " + PanelKey.Value + ", looks in " + Store.Dir);
            // The game destroys plugin components; a hidden object of our own keeps running.
            var go = new GameObject("SfMorphRunner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<Runner>().Init(this);
        }
    }

    // Runs late so the morphs land after the game's animation and its own
    // LateUpdate scripts (breast physics, lip sync) for the frame.
    [DefaultExecutionOrder(30000)]
    internal class Runner : MonoBehaviour
    {
        Plugin _p;
        Body _body;
        Panel _panel;
        bool _broken;

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
            }
            catch (Exception e) { Fail(e); }
        }

        void LateUpdate()
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
