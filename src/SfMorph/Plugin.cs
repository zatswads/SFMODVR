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
        public const string Version = "0.8.0";

        internal static BepInEx.Logging.ManualLogSource Log;
        internal ConfigEntry<KeyCode> PanelKey;
        internal ConfigEntry<bool> GateHidden, OverlapPlanted;
        internal ConfigEntry<float> OverlapRipple, ArmsBounce, LegsBounce, HeadBounce, SpineBounce, HandsLimbPush;
        internal ConfigEntry<int> BreastColliders;
        internal ConfigEntry<bool> KeysUnfocused, Apply, Physics, ShowSoftPoints, EmbodyOn, EmbodyHideHead, HandsOn, HandsCollide, GpuContactOn;
        // 0.7.8 hands: convex-hull body colliders, pushes from hands that aren't physics hands, friction, jointed fingers
        internal ConfigEntry<bool> HandsHull, HandsPushByTouch, HandsPushByPartner, HandsJointed;
        internal ConfigEntry<float> HandsFriction;
        internal ConfigEntry<float> UiScale, Softness, Springiness, Gravity, OverlapAmount, OverlapArms, OverlapLegs, OverlapHead, OverlapSpine;
        internal ConfigEntry<int> EmbodyHands, EmbodyView, EmbodyPartner, HandsMesh, Quality;
        // 0.7.4 procedural skin wrinkles
        internal ConfigEntry<bool> WrinkOn, WrinkFace, WrinkNeck, WrinkBody;
        internal ConfigEntry<float> WrinkStrength, WrinkThreshold, WrinkAttack, WrinkRelease, WrinkSpacingFace, WrinkSpacingBody, WrinkCullDistance;
        internal ConfigEntry<int> WrinkDebug, WrinkResolution;
        // 0.7.9 reactions
        internal ConfigEntry<bool> ReactOn, ReactVoice, ReactFace, ReactFlinch, ReactSound, ReactBlush, ReactBlushBody, ReactGaze, ReactFeedDesire, ReactAuto, ReactClimax, VoiceGuard;
        internal ConfigEntry<float> ReactSensitivity, ReactCooldown, ReactVolume, ReactBlushGain, ReactVoicePitchVar;
        internal ConfigEntry<int> ReactVoiceMode, ReactLog;
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
            Quality = Config.Bind("Physics", "Quality", 1, new ConfigDescription("Soft zones: 0 Performance (few points), 1 Balanced (dense points), 2 Fidelity (dense points + smoothed, subdivided body mesh).", new AcceptableValueRange<int>(0, 2)));
            OverlapAmount = Config.Bind("Motion", "Overlap", 0.3f, new ConfigDescription("Secondary motion: her limbs, head and spine trail the animation a little (0 off).", new AcceptableValueRange<float>(0f, 1f)));
            OverlapArms = Config.Bind("Motion", "Arms", 1f, new ConfigDescription("Overlap amount on the arms.", new AcceptableValueRange<float>(0f, 1f)));
            OverlapLegs = Config.Bind("Motion", "Legs", 1f, new ConfigDescription("Overlap amount on the legs.", new AcceptableValueRange<float>(0f, 1f)));
            OverlapHead = Config.Bind("Motion", "Head", 1f, new ConfigDescription("Overlap amount on the neck and head.", new AcceptableValueRange<float>(0f, 1f)));
            OverlapSpine = Config.Bind("Motion", "Spine", 1f, new ConfigDescription("Overlap amount on the spine.", new AcceptableValueRange<float>(0f, 1f)));
            Gravity = Config.Bind("Physics", "Gravity", 0.5f, new ConfigDescription("Soft zones settle toward the floor as she moves (breasts most, thighs least): they hang when she stands, flatten on her back, drop when she bends over. 0 off.", new AcceptableValueRange<float>(0f, 1f)));
            GpuContactOn = Config.Bind("Physics", "GpuContact", true, "Per-vertex contact on the GPU (shader bundle sfmorph_shaders next to the DLL): her skin is pressed exactly around hands and fingers.");
            GateHidden = Config.Bind("Physics", "GateHidden", true, "Hands that aren't drawn (a hidden partner, controller models replaced by the physics hands) don't touch her.");
            BreastColliders = Config.Bind("Physics", "BreastColliders", 1, new ConfigDescription("Hands push the game's own breast physics: 0 off, 1 the main breast chains, 2 every breast DynamicBone.", new AcceptableValueRange<int>(0, 2)));
            OverlapRipple = Config.Bind("Motion", "Ripple", 0.5f, new ConfigDescription("Motion travels down the chains: a fast turn of the torso swings the head and arms a little later (0 off).", new AcceptableValueRange<float>(0f, 1f)));
            ArmsBounce = Config.Bind("Motion", "ArmsBounce", 0.5f, new ConfigDescription("How much the arms overshoot and settle (0 damped, 1 bouncy).", new AcceptableValueRange<float>(0f, 1f)));
            LegsBounce = Config.Bind("Motion", "LegsBounce", 0.4f, new ConfigDescription("How much the legs overshoot and settle.", new AcceptableValueRange<float>(0f, 1f)));
            HeadBounce = Config.Bind("Motion", "HeadBounce", 0.6f, new ConfigDescription("How much the head and neck overshoot and settle.", new AcceptableValueRange<float>(0f, 1f)));
            SpineBounce = Config.Bind("Motion", "SpineBounce", 0.3f, new ConfigDescription("How much the spine overshoots and settles.", new AcceptableValueRange<float>(0f, 1f)));
            OverlapPlanted = Config.Bind("Motion", "PlantedLock", true, "Planted hands and feet stay where the animation puts them while the rest of the limb trails.");
            HandsLimbPush = Config.Bind("Hands", "LimbPush", 0.5f, new ConfigDescription("Hands push her limbs, head and spine aside; the pose springs back (0 off).", new AcceptableValueRange<float>(0f, 1f)));
            HandsPushByPartner = Config.Bind("Hands", "PushByPartner", false, "The partner's animated hands push her too (off: only your hands and the partner you embody; his animations often hold her arms, and a push would move them out of his grip).");
            HandsPushByTouch = Config.Bind("Hands", "PushByTouch", true, "Hands that aren't physics hands (your hand models, the partner's hands) push her limbs by how deep they are in her; physics hands push by contact force.");
            HandsHull = Config.Bind("Hands", "HullColliders", true, "Her body colliders follow her skin's shape (convex hulls) instead of capsules.");
            HandsFriction = Config.Bind("Hands", "Friction", 0.5f, new ConfigDescription("How much your hands grip her skin: a resting hand is carried along when she moves (0 slides freely). Sliding and sticking friction are the same (0.75 x this), so a hand drags instead of clinging and breaking loose.", new AcceptableValueRange<float>(0f, 1f)));
            HandsJointed = Config.Bind("Hands", "JointedFingers", true, "Fingers are simulated bodies on joints (off: posed by the grip only, like 0.7.0).");
            ShowSoftPoints = Config.Bind("Physics", "ShowSoftPoints", false, "Small markers on the soft points (green at rest, red touched) and the contact capsules, to see what touches what.");
            WrinkOn = Config.Bind("Wrinkles", "On", false, "Procedural skin wrinkles: folds appear in her skin's normal map where the skin compresses (expressions, bending), computed on the GPU (shader bundle sfmorph_shaders next to the DLL).");
            WrinkStrength = Config.Bind("Wrinkles", "Strength", 1f, new ConfigDescription("How deep the folds look (0 none, 2 strong).", new AcceptableValueRange<float>(0f, 2f)));
            WrinkThreshold = Config.Bind("Wrinkles", "Threshold", 0.025f, new ConfigDescription("How much the skin has to compress before a fold shows (lower = more folds).", new AcceptableValueRange<float>(0.01f, 0.06f)));
            WrinkFace = Config.Bind("Wrinkles", "Face", true, "Wrinkles on her face skin.");
            WrinkNeck = Config.Bind("Wrinkles", "Neck", true, "Wrinkles on her neck.");
            WrinkBody = Config.Bind("Wrinkles", "Body", false, "Wrinkles on the rest of her body skin (a bigger skin texture to rebuild).");
            WrinkDebug = Config.Bind("Wrinkles", "Debug", 0, new ConfigDescription("0 off, 1 show where skin compresses (red tint), 2 show the fold direction.", new AcceptableValueRange<int>(0, 2)));
            WrinkAttack = Config.Bind("Wrinkles", "Attack", 0.12f, new ConfigDescription("Seconds for a fold to appear.", new AcceptableValueRange<float>(0.01f, 1f)));
            WrinkRelease = Config.Bind("Wrinkles", "Release", 0.3f, new ConfigDescription("Seconds for a fold to fade away.", new AcceptableValueRange<float>(0.01f, 2f)));
            WrinkSpacingFace = Config.Bind("Wrinkles", "SpacingFace", 0.004f, new ConfigDescription("Distance between folds on the face in metres (applies at the next rebuild).", new AcceptableValueRange<float>(0.001f, 0.02f)));
            WrinkSpacingBody = Config.Bind("Wrinkles", "SpacingBody", 0.008f, new ConfigDescription("Distance between folds on the neck and body in metres (applies at the next rebuild).", new AcceptableValueRange<float>(0.002f, 0.04f)));
            WrinkResolution = Config.Bind("Wrinkles", "Resolution", 0, new ConfigDescription("Wrinkle texture size: 0 the skin texture's size (at most 4096), 1 at most 2048, 2 at most 1024.", new AcceptableValueRange<int>(0, 2)));
            WrinkCullDistance = Config.Bind("Wrinkles", "CullDistance", 3f, new ConfigDescription("No wrinkle updates while her head is farther than this from your headset (metres).", new AcceptableValueRange<float>(0.5f, 20f)));
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
            ReactOn = Config.Bind("Reactions", "On", false, "She reacts to touch: caress, grope and slaps (voice, face, flinch, slap sound, blush). Needs soft zones or physics hands.");
            ReactSensitivity = Config.Bind("Reactions", "Sensitivity", 0.5f, new ConfigDescription("0: only hard slaps count; 1: light taps too. At 0.5 a soft slap starts at 1.2 m/s, a hard one at 2.3, too hard at 4.5.", new AcceptableValueRange<float>(0f, 1f)));
            ReactCooldown = Config.Bind("Reactions", "Cooldown", 1.5f, new ConfigDescription("Seconds between her voice reactions at excitement tier 2 (shorter when more excited).", new AcceptableValueRange<float>(0.5f, 5f)));
            ReactVoice = Config.Bind("Reactions", "Voice", true, "She moans in reaction (only in sex or foreplay mode, never over dialogue or dirty talk).");
            ReactVoiceMode = Config.Bind("Reactions", "VoiceMode", 0, new ConfigDescription("0 the game picks the moan, 1 a moan chosen by intensity from her voice library.", new AcceptableValueRange<int>(0, 1)));
            ReactVoicePitchVar = Config.Bind("Reactions", "VoicePitchVar", 0.04f, new ConfigDescription("Random pitch change of the moans chosen by intensity (plus/minus).", new AcceptableValueRange<float>(0f, 0.15f)));
            ReactFace = Config.Bind("Reactions", "Face", true, "Her face reacts (wince, blink) when there is no voice.");
            ReactFlinch = Config.Bind("Reactions", "Flinch", true, "Her body swings and her soft zones jiggle after a slap (needs the limb push slider above 0).");
            ReactSound = Config.Bind("Reactions", "Sound", true, "A slap sound at the spot of a slap.");
            ReactVolume = Config.Bind("Reactions", "Volume", 0.6f, new ConfigDescription("Slap sound volume.", new AcceptableValueRange<float>(0f, 1f)));
            ReactBlush = Config.Bind("Reactions", "Blush", true, "Her face reddens with excitement and after slaps.");
            ReactBlushBody = Config.Bind("Reactions", "BlushBody", false, "Her body skin reddens too, at half strength.");
            ReactBlushGain = Config.Bind("Reactions", "BlushGain", 0.6f, new ConfigDescription("How strong the redness is.", new AcceptableValueRange<float>(0f, 2f)));
            ReactGaze = Config.Bind("Reactions", "Gaze", false, "She looks away for a moment after a hard hit.");
            ReactFeedDesire = Config.Bind("Reactions", "FeedDesire", false, "Touch also raises the game's own desire gauge (at most every 3 s, in sex or foreplay mode).");
            ReactAuto = Config.Bind("Reactions", "AutoState", true, "States (aroused, startled, guarded, afterglow) change by themselves.");
            ReactClimax = Config.Bind("Reactions", "AllowClimax", false, "At full excitement she climaxes (cosmetic: three moans, then afterglow).");
            VoiceGuard = Config.Bind("Reactions", "VoiceGuard", true, "Repair the game's dirty-talk voice error (IndexOutOfRange in PlayRandomVoice every frame). Works without Reactions.");
            ReactLog = Config.Bind("Reactions", "Log", 1, new ConfigDescription("0 quiet, 1 the first 20 events then every 10th, 2 every event.", new AcceptableValueRange<int>(0, 2)));
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
