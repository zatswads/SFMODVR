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
        public const string Version = "0.11.46";

        internal static BepInEx.Logging.ManualLogSource Log;
        internal ConfigEntry<KeyCode> PanelKey;
        internal ConfigEntry<bool> GateHidden, OverlapPlanted, HandsYieldLock;
        internal ConfigEntry<float> OverlapRipple, ArmsBounce, LegsBounce, HeadBounce, SpineBounce, HandsLimbPush;
        internal ConfigEntry<int> BreastColliders;
        internal ConfigEntry<bool> KeysUnfocused, Apply, Physics, ShowSoftPoints, EmbodyOn, EmbodyHideHead, HandsOn, HandsCollide, GpuContactOn;
        // 0.7.8 hands: convex-hull body colliders, pushes from hands that aren't physics hands, friction, jointed fingers
        internal ConfigEntry<bool> HandsHull, HandsPushByTouch, HandsPushByPartner, HandsJointed, HandsDrawnFade, HandsFingerConform, EmbodyNeutralFingers, EmbodyElbowAvoid;
        internal ConfigEntry<float> HandsRestCurl, EmbodyViewYaw, EmbodyViewPitch, EmbodyViewRoll, OverlapStrength, EmbodyViewForward, EmbodyViewUp, EmbodyViewRight;
        internal ConfigEntry<bool> EmbodyAtStart;
        internal ConfigEntry<int> HandsGrabButton, EmbodyViewAlign;
        internal ConfigEntry<float> HandsFriction;
        internal ConfigEntry<float> UiScale, Softness, Springiness, Gravity, OverlapAmount, OverlapArms, OverlapLegs, OverlapHead, OverlapSpine;
        internal ConfigEntry<int> EmbodyHands, EmbodyView, EmbodyPartner, HandsMesh, Quality;
        // 0.7.4 procedural skin wrinkles
        internal ConfigEntry<bool> WrinkOn, WrinkFace, WrinkNeck, WrinkBody;
        internal ConfigEntry<float> WrinkStrength, WrinkThreshold, WrinkAttack, WrinkRelease, WrinkCullDistance, WrinkCavity;
        internal ConfigEntry<int> WrinkDebug, WrinkResolution;
        internal ConfigEntry<bool> WrinkCurveDrive;   // 0.11.47
        internal ConfigEntry<float> WrinkCurveGain;
        // 0.10.0 muscle definition (a layer of the wrinkle sheets)
        internal ConfigEntry<bool> MuscleOn, MuscleCore, MuscleBack, MuscleLegs, MuscleArms;
        internal ConfigEntry<float> MuscleFitness, MuscleGain, MuscleStrength, MuscleShade, MuscleAttack, MuscleRelease;
        internal ConfigEntry<int> MuscleDebug;
        // 0.11.8 tensed muscles as geometry (GPU contact v2) and firmness under a hand
        internal ConfigEntry<float> RippleThrustAmt, ContactNormalSmooth;
        internal ConfigEntry<float> MuscleFlexAmt, MuscleCoreMm, MuscleBackMm, MuscleLegsMm, MuscleArmsMm, MuscleFirmness;
        // 0.7.9 reactions
        internal ConfigEntry<bool> ReactOn, ReactVoice, ReactFace, ReactFlinch, ReactSound, ReactBlush, ReactBlushBody, ReactGaze, ReactFeedDesire, ReactAuto, ReactClimax, VoiceGuard, ReactLines, ReactFaceClips, ReactLinesGamePath, ReactFoley;
        internal ConfigEntry<float> ReactSensitivity, ReactCooldown, ReactVolume, ReactSkinVolume, ReactBlushGain, ReactVoicePitchVar, ReactLineChance, ReactFaceStrength, VoicePitch, VoiceFormant;
        internal ConfigEntry<string> ReactFaceMap, VoicePackName;
        internal ConfigEntry<bool> LipSyncOn;
        internal ConfigEntry<float> LipStrength, LipLead, LipOverGame, LipMood, LipBrows, LipSticky;
        internal ConfigEntry<int> ReactVoiceMode, ReactLog, ReactConfigRev;
        // 0.8.0 contact visuals v2 (GPU contact: dent edge, rim bulge, bent normals, self-contact, partner body, bend bulges, ripple, skin marks)
        internal ConfigEntry<bool> VoiceMuteDialogue, VoicePackInScenes, VoiceNonEnglish;
        internal ConfigEntry<bool> ContactNormals, SelfContact, PartnerBody, SkinMarksOn, ReactRubSound, VoiceMuteGame, RippleThrust;
        // 0.11.28 Delta Mush at her joints
        internal ConfigEntry<bool> DeltaMush;
        internal ConfigEntry<float> DeltaMushAmount, DeltaMushRadius;
        // 0.11.31 skin folds at deep bends, chubby (soft fat by area)
        internal ConfigEntry<float> SkinFolds, FoldElbowMm, FoldKneeMm, FoldHipMm, Chubby;
        internal ConfigEntry<float>[] ChubbyArea;
        internal ConfigEntry<float> BuildStrength, BuildFat;   // the build triangle's last pick (weights; skinny = 1 - both)
        internal static readonly string[] ChubbyNames = { "Belly", "Waist", "Back", "Glutes", "Thighs", "Arms", "Calves", "Face" };
        internal ConfigEntry<bool> ReactPenSound, ReactPenReact;   // 0.11.25
        internal ConfigEntry<bool> HapticsOn;                      // 0.11.26
        internal ConfigEntry<float> HapticsStrength, HapticsTouch, HapticsImpact, HapticsGrab, HapticsPen;
        internal ConfigEntry<int> HapticsPath, HapticsLog;
        internal ConfigEntry<float> ReactPenVolume;
        internal ConfigEntry<float> ContactSkirt, ContactBulge, ContactDepth, BendBulge, BendBicepsMm, BendKneeMm, BendCalfMm, BendHipMm, BendBellyFoldMm, BendWaistRollMm,
            RippleAmount, RippleSpeed, RippleWavelength, RippleDamping, RippleImpact, MarksRedness, MarksFade, MarksAo, MarksBoost,
            MarksPrint, MarksPrintTime, MarksSmooth, MuscleObliques;   // 0.11.20
        internal ConfigEntry<Vector3> LeftHandPos, LeftHandRot, RightHandPos, RightHandRot;
        internal ConfigEntry<string> PanelRect;
        internal ConfigEntry<int> Tab;
        internal ConfigEntry<bool> PerfLog;

        void Awake()
        {
            Log = Logger;
            PanelKey = Config.Bind("Interface", "PanelKey", KeyCode.F8, "Body morphs panel in the desktop window.");
            KeysUnfocused = Config.Bind("Interface", "KeysWhenUnfocused", true, "The key also works while another window (Virtual Desktop, SteamVR) has focus.");
            UiScale = Config.Bind("Interface", "PanelScale", 1.25f, new ConfigDescription("Size of the panel's text and controls.", new AcceptableValueRange<float>(0.75f, 2.5f)));
            PanelRect = Config.Bind("Interface", "PanelRect", "", "Panel position and size (x,y,width,height), saved when you move or resize it.");
            Tab = Config.Bind("Interface", "Tab", 0, "Last panel tab.");
            PerfLog = Config.Bind("General", "PerfLog", true, "Every 10 s a 'Perf' line in the log (and at the top of the panel): what each part of SF Body Morphs costs per frame (avg/max ms), the frame rate, fixed steps per frame, the GPU time when the VR runtime reports it, and where the rest of the frame goes (the game's physics, rendering, waiting). Costs about 0.01 ms a frame.");
            Avatar.Bind(Config);
            Apply = Config.Bind("General", "Apply", true, "Apply the body changes (off = the game's own look, for comparing).");
            Physics = Config.Bind("Physics", "SoftZones", true, "Hands and limbs press into her glutes, thighs, belly and breasts (helper bones driven by the game's DynamicBone).");
            Softness = Config.Bind("Physics", "Softness", 0.5f, new ConfigDescription("How much the soft zones give (0 firm, 1 soft).", new AcceptableValueRange<float>(0f, 1f)));
            Springiness = Config.Bind("Physics", "Springiness", 0.45f, new ConfigDescription("Secondary motion of the soft zones from her own movement (0 none, 1 a lot).", new AcceptableValueRange<float>(0f, 1f)));
            Quality = Config.Bind("Physics", "Quality", 2, new ConfigDescription("Soft zones: 0 Performance (few points), 1 Balanced (dense points), 2 Fidelity (dense points + smoothed, subdivided body mesh).", new AcceptableValueRange<int>(0, 2)));
            OverlapAmount = Config.Bind("Motion", "Overlap", 0.3f, new ConfigDescription("Secondary motion: her limbs, head and spine trail the animation a little (0 off).", new AcceptableValueRange<float>(0f, 1f)));
            OverlapStrength = Config.Bind("Motion", "Strength", 1f, new ConfigDescription("Multiplies the secondary motion on top of Overlap (1 = as designed, up to 4x: bigger trailing and swings; the springs themselves don't change, so it stays stable).", new AcceptableValueRange<float>(1f, 4f)));
            OverlapArms = Config.Bind("Motion", "Arms", 1f, new ConfigDescription("Overlap amount on the arms.", new AcceptableValueRange<float>(0f, 1f)));
            OverlapLegs = Config.Bind("Motion", "Legs", 1f, new ConfigDescription("Overlap amount on the legs.", new AcceptableValueRange<float>(0f, 1f)));
            OverlapHead = Config.Bind("Motion", "Head", 1f, new ConfigDescription("Overlap amount on the neck and head.", new AcceptableValueRange<float>(0f, 1f)));
            OverlapSpine = Config.Bind("Motion", "Spine", 1f, new ConfigDescription("Overlap amount on the spine.", new AcceptableValueRange<float>(0f, 1f)));
            Gravity = Config.Bind("Physics", "Gravity", 0.5f, new ConfigDescription("Soft zones settle toward the floor as she moves (breasts most, thighs least): they hang when she stands, flatten on her back, drop when she bends over. 0 off.", new AcceptableValueRange<float>(0f, 1f)));
            GpuContactOn = Config.Bind("Physics", "GpuContact", true, "Per-vertex contact on the GPU (shader bundle sfmorph_shaders next to the DLL): her skin is pressed exactly around hands and fingers.");
            ContactSkirt = Config.Bind("Physics", "ContactSkirt", 0.015f, new ConfigDescription("GPU contact: how far around a hand the skin still follows the dent (metres; 0.005 = the old hard edge).", new AcceptableValueRange<float>(0.005f, 0.04f)));
            ContactBulge = Config.Bind("Physics", "ContactBulge", 0.6f, new ConfigDescription("GPU contact: how much the pressed skin bulges up around the dent (0 none).", new AcceptableValueRange<float>(0f, 1f)));
            ContactNormals = Config.Bind("Physics", "ContactNormals", true, "GPU contact: the skin's shading follows the dent (bent normals), so a press reads as pressed flesh.");
            ContactDepth = Config.Bind("Physics", "ContactDepth", 1f, new ConfigDescription("GPU contact: depth scale of the per-vertex dent on soft parts (1 = 1-2.5 cm by Softness; bony parts stay at 2-8 mm).", new AcceptableValueRange<float>(0.3f, 2f)));
            SelfContact = Config.Bind("Physics", "SelfContact", true, "Her own limbs press her skin: thighs together, calf on thigh when kneeling, arm on breast and side.");
            RippleThrust = Config.Bind("Physics", "RippleThrust", true, "Every stroke of the partner's hips against her (0.35 m/s or faster) sends a ripple, not only a first touch (needs PartnerBody).");
            RippleThrustAmt = Config.Bind("Physics", "RippleThrustAmt", 1f, new ConfigDescription("Size of the ripple from each stroke of his hips (1 = 8 mm for a slow stroke up to 20 mm for a fast one; 0 off).", new AcceptableValueRange<float>(0f, 3f)));
            ContactNormalSmooth = Config.Bind("Physics", "ContactNormalSmooth", 8f, new ConfigDescription("GPU contact: how far the dent's shading is smoothed (mm). Larger = rounder, less faceted pressed-flesh shading; 0 = the exact field (0.11.11 look).", new AcceptableValueRange<float>(0f, 20f)));
            DeltaMush = Config.Bind("Physics", "DeltaMush", true, "GPU contact: her elbows, knees, hips and shoulders keep their volume in deep bends instead of pinching and flattening (Delta Mush on the skinned body: the joint skin is smoothed, then its rest detail put back). Costs GPU time every frame she is visible.");
            DeltaMushAmount = Config.Bind("Physics", "DeltaMushAmount", 1f, new ConfigDescription("Strength of the Delta Mush at her joints (0 = the plain skinning).", new AcceptableValueRange<float>(0f, 1f)));
            DeltaMushRadius = Config.Bind("Physics", "DeltaMushRadius", 20f, new ConfigDescription("How far the Delta Mush smooths around each joint (mm). Larger = rounder bends, more GPU passes (at most 48). Applies when her body is rebuilt (character change, Rescan, restart).", new AcceptableValueRange<float>(5f, 60f)));
            SkinFolds = Config.Bind("Physics", "SkinFolds", 1f, new ConfigDescription("GPU contact: her skin folds in at deeply bent joints (a crease with the flesh rolling up on both sides): inner elbow, back of the knee, groin when sitting (0 off).", new AcceptableValueRange<float>(0f, 2f)));
            FoldElbowMm = Config.Bind("Physics", "FoldElbowMm", 4f, new ConfigDescription("Inner-elbow fold depth at a fully bent elbow (mm, before SkinFolds).", new AcceptableValueRange<float>(0f, 15f)));
            FoldKneeMm = Config.Bind("Physics", "FoldKneeMm", 5f, new ConfigDescription("Back-of-the-knee fold depth at a fully bent knee (mm, before SkinFolds).", new AcceptableValueRange<float>(0f, 15f)));
            FoldHipMm = Config.Bind("Physics", "FoldHipMm", 5f, new ConfigDescription("Groin fold depth with the hip drawn up (sitting; mm, before SkinFolds).", new AcceptableValueRange<float>(0f, 15f)));
            Chubby = Config.Bind("Chubby", "Chubby", 0f, new ConfigDescription("GPU contact: soft fat on her body, the way the muscle shape makes her look muscular: the areas below x this (0 off). Fat hides the muscles under it, dents deeper, ripples more, and makes the belly / waist rolls and the folds beside her joints fuller. Below 0 she is slimmer in the same areas.", new AcceptableValueRange<float>(-1f, 2f)));
            float[] fatDef = { 14f, 12f, 8f, 14f, 14f, 10f, 7f, 6f };
            string[] fatDesc = { "the lower belly's pooch and a softer upper belly", "love handles over the hip bones", "the bra-line bulge and the lower back", "fuller, rounder glutes", "saddlebags and the inner thighs", "the whole arm, most at the backs of the upper arms", "the calves all round", "fuller cheeks, a double chin and a softer neck" };
            BuildStrength = Config.Bind("Chubby", "BuildStrength", 1f / 3f, new ConfigDescription("Skin tab build triangle: the last pick's Strength weight (the dot; sets nothing by itself).", new AcceptableValueRange<float>(0f, 1f)));
            BuildFat = Config.Bind("Chubby", "BuildFat", 1f / 3f, new ConfigDescription("Skin tab build triangle: the last pick's Fat weight (Skinny = 1 - Strength - Fat).", new AcceptableValueRange<float>(0f, 1f)));
            ChubbyArea = new ConfigEntry<float>[ChubbyNames.Length];
            for (int i = 0; i < ChubbyNames.Length; i++)
                ChubbyArea[i] = Config.Bind("Chubby", ChubbyNames[i], fatDef[i], new ConfigDescription("Chubby: " + fatDesc[i] + " (mm at the fullest spot, x Chubby).", new AcceptableValueRange<float>(0f, 40f)));
            PartnerBody = Config.Bind("Physics", "PartnerBody", true, "The partner's pelvis and thighs press her skin too (not only his hands).");
            BendBulge = Config.Bind("Physics", "BendBulge", 0.6f, new ConfigDescription("Muscles and flesh bulge when joints bend: biceps with the elbow, thigh and calf with the knee, thigh tops and a belly fold when sitting (0 off).", new AcceptableValueRange<float>(0f, 1f)));
            BendBicepsMm = Config.Bind("Physics", "BendBicepsMm", 8f, new ConfigDescription("Biceps bulge at a fully bent elbow (mm, before BendBulge).", new AcceptableValueRange<float>(0f, 20f)));
            BendKneeMm = Config.Bind("Physics", "BendKneeMm", 8f, new ConfigDescription("Back-of-thigh bulge at a fully bent knee (mm).", new AcceptableValueRange<float>(0f, 20f)));
            BendCalfMm = Config.Bind("Physics", "BendCalfMm", 6f, new ConfigDescription("Calf bulge at a fully bent knee (mm).", new AcceptableValueRange<float>(0f, 20f)));
            BendHipMm = Config.Bind("Physics", "BendHipMm", 5f, new ConfigDescription("Thigh-top bulge when the hip flexes (sitting, mm).", new AcceptableValueRange<float>(0f, 20f)));
            BendBellyFoldMm = Config.Bind("Physics", "BendBellyFoldMm", 3f, new ConfigDescription("Her belly rounds forward in a soft roll when both hips flex (sitting, squatting; mm before BendBulge).", new AcceptableValueRange<float>(0f, 20f)));
            BendWaistRollMm = Config.Bind("Physics", "BendWaistRollMm", 10f, new ConfigDescription("Her waist bunches into a soft roll above the hip on the side she bends toward, with a crease under it (mm at a full side bend, before BendBulge).", new AcceptableValueRange<float>(0f, 20f)));
            RippleAmount = Config.Bind("Physics", "Ripple", 1f, new ConfigDescription("An impact (a slap, a thrust against her glutes or thighs) sends a ripple through her flesh (0 off, 1 = 14 mm per m/s above 0.3 m/s up to 35 mm, 2 = twice that).", new AcceptableValueRange<float>(0f, 2f)));
            RippleSpeed = Config.Bind("Physics", "RippleSpeed", 0.5f, new ConfigDescription("How fast the ripple travels over her skin (m/s).", new AcceptableValueRange<float>(0.2f, 3f)));
            RippleWavelength = Config.Bind("Physics", "RippleWavelength", 0.1f, new ConfigDescription("Distance between ripple crests (m); a longer wave also reaches farther.", new AcceptableValueRange<float>(0.03f, 0.2f)));
            RippleDamping = Config.Bind("Physics", "RippleDamping", 0.6f, new ConfigDescription("How long a ripple lasts: seconds for it to fade to a third.", new AcceptableValueRange<float>(0.1f, 2f)));
            RippleImpact = Config.Bind("Physics", "RippleImpact", 0.35f, new ConfigDescription("How deep the first dip at the impact point is, as a share of the ripple (the rings that spread out keep their height): 1 = as deep as the rings could be, lower = a softer first hit.", new AcceptableValueRange<float>(0f, 1f)));
            SkinMarksOn = Config.Bind("Physics", "SkinMarks", true, "A hand pressed on her glutes, thighs, belly or breasts leaves a fading print; slaps leave redness.");
            MarksRedness = Config.Bind("Physics", "MarksRedness", 0.5f, new ConfigDescription("How red marks get (0 only the pressed shading).", new AcceptableValueRange<float>(0f, 1f)));
            MarksFade = Config.Bind("Physics", "MarksFade", 15f, new ConfigDescription("Seconds for redness to fade to a third.", new AcceptableValueRange<float>(3f, 60f)));
            MarksBoost = Config.Bind("Physics", "MarksBoost", 1f, new ConfigDescription("Multiplies how fast marks build up, how red they get and how much a press darkens the skin (1 = as designed).", new AcceptableValueRange<float>(1f, 4f)));
            MarksAo = Config.Bind("Physics", "MarksAo", 0.5f, new ConfigDescription("How much a live press darkens the skin (pressed-flesh shading).", new AcceptableValueRange<float>(0f, 1f)));
            // 0.11.20 (user: the print a hand leaves is polygonal and stays too long, and nothing sets it)
            MarksPrint = Config.Bind("Physics", "MarksPrint", 0.6f, new ConfigDescription("How deep the print a lifted hand leaves shows (its shading and paleness), as a share of how deep the hand pressed (0 none, 1 the full depth).", new AcceptableValueRange<float>(0f, 1.5f)));
            MarksPrintTime = Config.Bind("Physics", "MarksPrintTime", 0.8f, new ConfigDescription("Seconds for a hand's print to fade to a third (it was a fixed 1.5 s).", new AcceptableValueRange<float>(0.2f, 5f)));
            MarksSmooth = Config.Bind("Physics", "MarksSmooth", 6f, new ConfigDescription("How far the marks are smoothed on her skin (mm): rounds off the pressed shading, the print and the redness so they no longer follow the mesh's triangles (0 = off, the 0.11.19 look).", new AcceptableValueRange<float>(0f, 15f)));
            GateHidden = Config.Bind("Physics", "GateHidden", true, "Hands that aren't drawn (a hidden partner, controller models replaced by the physics hands) don't touch her.");
            BreastColliders = Config.Bind("Physics", "BreastColliders", 1, new ConfigDescription("Hands push the game's own breast physics: 0 off, 1 the main breast chains, 2 every breast DynamicBone.", new AcceptableValueRange<int>(0, 2)));
            OverlapRipple = Config.Bind("Motion", "Ripple", 0.5f, new ConfigDescription("Motion travels down the chains: a fast turn of the torso swings the head and arms a little later (0 off).", new AcceptableValueRange<float>(0f, 1f)));
            ArmsBounce = Config.Bind("Motion", "ArmsBounce", 0.5f, new ConfigDescription("How much the arms overshoot and settle (0 damped, 1 bouncy).", new AcceptableValueRange<float>(0f, 1f)));
            LegsBounce = Config.Bind("Motion", "LegsBounce", 0.4f, new ConfigDescription("How much the legs overshoot and settle.", new AcceptableValueRange<float>(0f, 1f)));
            HeadBounce = Config.Bind("Motion", "HeadBounce", 0.6f, new ConfigDescription("How much the head and neck overshoot and settle.", new AcceptableValueRange<float>(0f, 1f)));
            SpineBounce = Config.Bind("Motion", "SpineBounce", 0.3f, new ConfigDescription("How much the spine overshoots and settles.", new AcceptableValueRange<float>(0f, 1f)));
            OverlapPlanted = Config.Bind("Motion", "PlantedLock", true, "Planted hands and feet stay where the animation puts them while the rest of the limb trails.");
            HandsYieldLock = Config.Bind("Hands", "YieldLock", false, "While a hand pushes one of her arms or legs, her hand or foot stays where the animation puts it: the elbow or knee bends instead (off: a push on the forearm or calf moves the hand or foot too).");
            HandsLimbPush = Config.Bind("Hands", "LimbPush", 0.5f, new ConfigDescription("Her limbs, head and spine yield to a hand that presses into them and settle back without bouncing (0 off, 0.5 natural, 1 twice as far).", new AcceptableValueRange<float>(0f, 1f)));
            HandsPushByPartner = Config.Bind("Hands", "PushByPartner", false, "The partner's animated hands push her too (not the one you embody: his hands are your controllers; his animations often hold her arms, and a push would move them out of his grip).");
            HandsPushByTouch = Config.Bind("Hands", "PushByTouch", true, "Hands that aren't physics hands (your hand models, the partner's hands) push her limbs by how deep they are in her; physics hands push by contact force.");
            HandsHull = Config.Bind("Hands", "HullColliders", true, "Her body colliders follow her skin's shape (convex hulls) instead of capsules.");
            HandsFriction = Config.Bind("Hands", "Friction", 0.5f, new ConfigDescription("How much your hands grip her skin: a resting hand is carried along when she moves (0 slides freely). Sliding and sticking friction are the same (0.75 x this), so a hand drags instead of clinging and breaking loose.", new AcceptableValueRange<float>(0f, 1f)));
            HandsJointed = Config.Bind("Hands", "JointedFingers", true, "Fingers are simulated bodies on joints (off: posed by the grip only, like 0.7.0).");
            HandsDrawnFade = Config.Bind("Hands", "DrawnOffsetFade", true, "The drawn physics hand is exactly the controller in free air; the blocked displacement fades in on contact (off: always the physics pose, for comparing).");
            HandsFingerConform = Config.Bind("Hands", "FingerConform", true, "Fingers close until they touch her and sink in by the part's softness (off: the grip alone poses them).");
            HandsGrabButton = Config.Bind("Hands", "GrabButton", 0, new ConfigDescription("The controller button that closes the hand (all fingers together): 0 trigger (the grip is the game's menu button), 1 grip, 2 whichever is pressed further.", new AcceptableValueRange<int>(0, 2)));
            HandsRestCurl = Config.Bind("Hands", "RestCurl", 0.25f, new ConfigDescription("How much the fingers curl with nothing pressed (0 flat, 1 a fist). Physics hands and his hands.", new AcceptableValueRange<float>(0f, 1f)));
            ShowSoftPoints = Config.Bind("Physics", "ShowSoftPoints", false, "Small markers on the soft points (green at rest, red touched) and the contact capsules, to see what touches what.");
            WrinkOn = Config.Bind("Wrinkles", "On", false, "Skin wrinkles: authored expression lines and joint creases fade into her skin's normal map where the skin compresses across them, measured on the GPU (shader bundle sfmorph_shaders next to the DLL).");
            WrinkStrength = Config.Bind("Wrinkles", "Strength", 1f, new ConfigDescription("How deep the lines look (0 none, 2 strong).", new AcceptableValueRange<float>(0f, 2f)));
            WrinkThreshold = Config.Bind("Wrinkles", "Threshold", 0.025f, new ConfigDescription("How much the skin has to compress across a region's lines before they show (lower = sooner).", new AcceptableValueRange<float>(0.01f, 0.06f)));
            WrinkFace = Config.Bind("Wrinkles", "Face", true, "Wrinkles on her face skin.");
            WrinkNeck = Config.Bind("Wrinkles", "Neck", true, "Wrinkles on her neck.");
            WrinkBody = Config.Bind("Wrinkles", "Body", false, "Creases at her joints (inside the elbows, behind the knees, hip folds, armpits, belly).");
            WrinkDebug = Config.Bind("Wrinkles", "Debug", 0, new ConfigDescription("0 off, 1 tint active lines red, 2 show every region in its own colour.", new AcceptableValueRange<int>(0, 2)));
            WrinkAttack = Config.Bind("Wrinkles", "Attack", 0.12f, new ConfigDescription("Seconds for lines to appear.", new AcceptableValueRange<float>(0.01f, 1f)));
            WrinkRelease = Config.Bind("Wrinkles", "Release", 0.3f, new ConfigDescription("Seconds for lines to fade away.", new AcceptableValueRange<float>(0.01f, 2f)));
            WrinkResolution = Config.Bind("Wrinkles", "Resolution", 0, new ConfigDescription("Wrinkle texture size: 0 the skin texture's size (at most 4096), 1 at most 2048, 2 at most 1024.", new AcceptableValueRange<int>(0, 2)));
            WrinkCavity = Config.Bind("Wrinkles", "Cavity", 0.3f, new ConfigDescription("Crease shade: how much darker her skin colour is at the bottom of a line (0 off, 1 strong). Her skin's subsurface scattering blurs normal-map detail, most of all on her face; the darker crease stays visible.", new AcceptableValueRange<float>(0f, 1f)));
            WrinkCullDistance = Config.Bind("Wrinkles", "CullDistance", 3f, new ConfigDescription("No wrinkle updates while her head is farther than this from your headset (metres).", new AcceptableValueRange<float>(0.5f, 20f)));
            // 0.11.47 curve-driven face wrinkles (read by Wrinkles.UpdateActs through WrinkleSettings' statics)
            WrinkCurveDrive = Config.Bind("Wrinkles", "CurveDrive", true, "Face and neck lines also follow what her face is told to do (the lip sync's mouth / brow / nose curves, and an estimate per face-clip mood: smile, pain, pleasure, surprise...), not only how far her skin compresses. Expressions she holds keep their lines.");
            WrinkCurveGain = Config.Bind("Wrinkles", "CurveGain", 1f, new ConfigDescription("How strongly those curves fade the lines in (0 off, 2 double).", new AcceptableValueRange<float>(0f, 2f)));
            WrinkleSettings.CurveDrive = WrinkCurveDrive.Value; WrinkleSettings.CurveGain = WrinkCurveGain.Value;
            WrinkCurveDrive.SettingChanged += (o, e) => WrinkleSettings.CurveDrive = WrinkCurveDrive.Value;
            WrinkCurveGain.SettingChanged += (o, e) => WrinkleSettings.CurveGain = WrinkCurveGain.Value;
            MuscleOn = Config.Bind("Muscles", "On", false, "Muscle definition: authored muscle relief (abs, obliques, serratus, ribcage, back, lats, glutes, thighs, calves, shoulders, arms) in her skin's normal map, a faint resting tone everywhere and more where her pose tenses a muscle (shares the wrinkles' textures and shader bundle).");
            MuscleFitness = Config.Bind("Muscles", "Fitness", 0.3f, new ConfigDescription("Resting tone: how defined her muscles are when relaxed (0 none, 1 fully).", new AcceptableValueRange<float>(0f, 1f)));
            MuscleGain = Config.Bind("Muscles", "TensionGain", 1f, new ConfigDescription("How much her pose adds on top (a crunch for the abs, a squat for the thighs and glutes, a bent elbow for the biceps, tiptoes for the calves; 0 = resting tone only, 2 = full definition at half the tension).", new AcceptableValueRange<float>(0f, 2f)));
            MuscleStrength = Config.Bind("Muscles", "Strength", 1f, new ConfigDescription("Depth of the muscle relief (0 none, 2 strong).", new AcceptableValueRange<float>(0f, 2f)));
            MuscleShade = Config.Bind("Muscles", "Shade", 0.3f, new ConfigDescription("Groove shade: how much darker her skin colour is in the grooves between muscles (0 off, 1 strong).", new AcceptableValueRange<float>(0f, 1f)));
            MuscleCore = Config.Bind("Muscles", "Core", true, "Abs, obliques, serratus, ribcage.");
            MuscleBack = Config.Bind("Muscles", "Back", true, "Spine (erectors), lats, shoulder blades, traps.");
            MuscleLegs = Config.Bind("Muscles", "Legs", true, "Glutes, quads, hamstrings, adductors, calves.");
            MuscleArms = Config.Bind("Muscles", "Arms", true, "Deltoids, biceps, triceps, forearms, pecs.");
            MuscleDebug = Config.Bind("Muscles", "Debug", 0, new ConfigDescription("0 off, 1 tint the muscles green (relaxed) to red (tensed), 2 show every muscle in its own colour.", new AcceptableValueRange<int>(0, 2)));
            MuscleAttack = Config.Bind("Muscles", "Attack", 0.25f, new ConfigDescription("Seconds for a muscle to tense.", new AcceptableValueRange<float>(0.02f, 2f)));
            MuscleRelease = Config.Bind("Muscles", "Release", 0.8f, new ConfigDescription("Seconds for a muscle to relax.", new AcceptableValueRange<float>(0.02f, 3f)));
            MuscleFlexAmt = Config.Bind("Muscles", "Flex", 1f, new ConfigDescription("Tensed muscles bulge as geometry (needs GPU contact): the per-area sizes below x this (0 off).", new AcceptableValueRange<float>(0f, 2f)));
            MuscleCoreMm = Config.Bind("Muscles", "CoreMm", 3f, new ConfigDescription("Bulge of a fully active abs / obliques / serratus muscle at the area's highest sculpted belly (mm, before Flex; the sculpt keeps the other bellies and grooves in proportion).", new AcceptableValueRange<float>(0f, 20f)));
            MuscleBackMm = Config.Bind("Muscles", "BackMm", 4f, new ConfigDescription("Bulge of the fully active spinal erectors / lats (mm, before Flex).", new AcceptableValueRange<float>(0f, 20f)));
            MuscleLegsMm = Config.Bind("Muscles", "LegsMm", 4f, new ConfigDescription("Bulge of the fully active glutes / quads / hamstrings / adductors / calves (mm, before Flex).", new AcceptableValueRange<float>(0f, 20f)));
            MuscleObliques = Config.Bind("Muscles", "Obliques", 0.6f, new ConfigDescription("The obliques (the pads over her hip bones and the waist's sides) against the rest of the core: scales their relief, groove shade and bulge (Core mm x this; 0 off, 1 as strong as the abs).", new AcceptableValueRange<float>(0f, 1.5f)));
            MuscleArmsMm = Config.Bind("Muscles", "ArmsMm", 4f, new ConfigDescription("Bulge of the fully active deltoids / biceps / triceps / forearms (mm, before Flex; the biceps also gets the elbow's bend bulge).", new AcceptableValueRange<float>(0f, 20f)));
            MuscleFirmness = Config.Bind("Muscles", "Firmness", 0.5f, new ConfigDescription("A tensed muscle is firmer under a hand: its soft depth x (1 - Firmness x activation), for the dent and the physics hands' resistance (0 off).", new AcceptableValueRange<float>(0f, 0.9f)));
            EmbodyOn = Config.Bind("Embody", "On", false, "See through the partner's eyes (VR), his head hidden, his hands on your controllers.");
            EmbodyHands = Config.Bind("Embody", "Hands", 3, new ConfigDescription("His hands follow the controllers: 0 none, 1 left, 2 right, 3 both.", new AcceptableValueRange<int>(0, 3)));
            EmbodyHideHead = Config.Bind("Embody", "HideHead", true, "Hide his head so it doesn't block the view.");
            EmbodyViewAlign = Config.Bind("Embody", "ViewAlign", 1, new ConfigDescription("What the view is turned to on a snap: 0 his head's yaw, 1 his chest's (his arms then sit where yours are even when his head is turned).", new AcceptableValueRange<int>(0, 1)));
            EmbodyViewYaw = Config.Bind("Embody", "ViewYaw", 0f, new ConfigDescription("Turn correction of the view on a snap (degrees, + = turn right).", new AcceptableValueRange<float>(-90f, 90f)));
            EmbodyViewPitch = Config.Bind("Embody", "ViewPitch", 0f, new ConfigDescription("Tilt correction of the world on a snap (degrees, + = nose up). Tilting the world can be uncomfortable: small values.", new AcceptableValueRange<float>(-45f, 45f)));
            EmbodyViewRoll = Config.Bind("Embody", "ViewRoll", 0f, new ConfigDescription("Roll correction of the world on a snap (degrees).", new AcceptableValueRange<float>(-45f, 45f)));
            EmbodyViewForward = Config.Bind("Embody", "ViewForward", 0f, new ConfigDescription("View position: forward (+) or back from his eyes (m, his head's axes).", new AcceptableValueRange<float>(-0.2f, 0.2f)));
            EmbodyViewUp = Config.Bind("Embody", "ViewUp", 0f, new ConfigDescription("View position: up (+) or down from his eyes (m).", new AcceptableValueRange<float>(-0.2f, 0.2f)));
            EmbodyViewRight = Config.Bind("Embody", "ViewRight", 0f, new ConfigDescription("View position: right (+) or left of his eyes (m).", new AcceptableValueRange<float>(-0.2f, 0.2f)));
            // 0.11.7 (user): Embody starts off with every game start; the toggle is for this session only unless asked otherwise
            EmbodyAtStart = Config.Bind("Embody", "OnAtStart", false, "Keep Embody's on/off from the last session (off: Embody is always off when the game starts).");
            if (!EmbodyAtStart.Value && EmbodyOn.Value) EmbodyOn.Value = false;
            EmbodyNeutralFingers = Config.Bind("Embody", "NeutralFingers", true, "His fingers rest in a neutral pose instead of the animation's; the grab button (VR -> Grab with, the trigger by default) closes the whole hand and it stops on her skin.");
            EmbodyElbowAvoid = Config.Bind("Embody", "ElbowAvoid", true, "His elbow swings around her instead of his arm passing through her.");
            EmbodyView = Config.Bind("Embody", "View", 2, new ConfigDescription("0 the game's camera, 1 snap to his eyes once, 2 follow his eyes.", new AcceptableValueRange<int>(0, 2)));
            EmbodyPartner = Config.Bind("Embody", "Partner", 0, new ConfigDescription("0 the player, 1 the other man (badGuy).", new AcceptableValueRange<int>(0, 1)));
            HapticsOn = Config.Bind("Haptics", "On", false, "The controllers vibrate with what your hands touch on her: a tick or a slap pulse when a hand lands, a low buzz while it slides over her skin, a squeeze when you grab her, a buzz and pulses while a finger is in her. Quest Pro (Meta's runtime over Link): the grip, trigger and thumb actuators separately; Virtual Desktop and other runtimes: the main motor. Needs soft zones or physics hands (her body).");
            HapticsStrength = Config.Bind("Haptics", "Strength", 1f, new ConfigDescription("All haptics together (times each kind below).", new AcceptableValueRange<float>(0f, 1f)));
            HapticsTouch = Config.Bind("Haptics", "Touch", 0.5f, new ConfigDescription("Resting and sliding touches: the faint hold by how deep the hand presses, the buzz by slide speed (full at 40 cm/s), the onset tick.", new AcceptableValueRange<float>(0f, 1f)));
            HapticsImpact = Config.Bind("Haptics", "Impacts", 1f, new ConfigDescription("Pats and slaps: a short sharp pulse by how fast the hand lands (a pat from 0.45 m/s, a slap from 1.2, full at 3).", new AcceptableValueRange<float>(0f, 1f)));
            HapticsGrab = Config.Bind("Haptics", "Grab", 0.6f, new ConfigDescription("The grab button closing on her (a squeeze pulse) and the hold while it stays closed (grip actuator).", new AcceptableValueRange<float>(0f, 1f)));
            HapticsPen = Config.Bind("Haptics", "Penetration", 0.7f, new ConfigDescription("A finger in her vagina, anus or mouth: a buzz by depth and a pulse going in / out and on each stroke (trigger actuator).", new AcceptableValueRange<float>(0f, 1f)));
            HapticsPath = Config.Bind("Haptics", "Path", 0, new ConfigDescription("0 automatic (OpenXR actions: the Touch Pro actuators when the runtime has them, else one motor per controller; else the XR device), 1 always the XR device's channel 0 (if the automatic path stays silent).", new AcceptableValueRange<int>(0, 1)));
            HapticsLog = Config.Bind("Haptics", "Log", 1, new ConfigDescription("0 only the path, 1 also the first pulse of each kind and a 10 s summary, 2 every pulse and every buzz on / off.", new AcceptableValueRange<int>(0, 2)));
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
            ReactVoice = Config.Bind("Reactions", "Voice", true, "She moans in reaction (the game's moan in sex or foreplay mode, her own clips and breaths elsewhere; never over dialogue or dirty talk).");
            ReactVoiceMode = Config.Bind("Reactions", "VoiceMode", 0, new ConfigDescription("0 the game picks the moan, 1 a moan chosen by intensity from her voice library.", new AcceptableValueRange<int>(0, 1)));
            ReactVoicePitchVar = Config.Bind("Reactions", "VoicePitchVar", 0.04f, new ConfigDescription("Random pitch change of the moans chosen by intensity (plus/minus).", new AcceptableValueRange<float>(0f, 0.15f)));
            ReactLines = Config.Bind("Reactions", "Lines", true, "She speaks the game's own recorded lines for the touched area (breasts: nipple lines, between her legs: pussy or anal lines, butt, hips and thighs: her general lines), at the level of her excitement, and a feeling sound when her excitement rises a tier. Sex or foreplay mode only, never over dialogue, a kiss, the game's dirty talk or a moan.");
            ReactLinesGamePath = Config.Bind("Reactions", "LinesGamePath", true, "Her lines go through the game's own dirty-talk path (her talking face and the subtitle come with them; the game's dirty-talk state is put back right after). Off: the clip plays on the game's dirty-talk source, without face or subtitle.");
            ReactLineChance = Config.Bind("Reactions", "LineChance", 0.5f, new ConfigDescription("Chance that a voiced touch of an area with lines speaks a line instead of a moan (at most one line per 8 s at the default voice gap).", new AcceptableValueRange<float>(0f, 1f)));
            ReactFaceClips = Config.Bind("Reactions", "FaceClips", true, "Her face Animator's own clips play over her face as reactions (pleasure by excitement on breasts, hips, butt, thighs and neck; ticklish or surprised elsewhere; surprise on soft slaps, pain on hard ones; climax). Found by a one-time survey of her face clips (log and BepInEx\\config\\SfMorphs\\face-clips.txt). Her mouth is left to the game while she speaks or moans.");
            ReactFaceStrength = Config.Bind("Reactions", "FaceStrength", 0.8f, new ConfigDescription("How strongly the face clips show over the game's face.", new AcceptableValueRange<float>(0f, 1f)));
            ReactFaceMap = Config.Bind("Reactions", "FaceMap", "", "The face clip per mood chosen in the panel, 'Mood=clip;...'. Empty: picked by name, then by strength.");
            ReactFace = Config.Bind("Reactions", "Face", true, "Her face reacts to touch: the game's own eye reaction on slaps (and soft ones on caresses and gropes), with a wince or blink alongside when no moan carries the face.");
            ReactFlinch = Config.Bind("Reactions", "Flinch", true, "Her body swings and her soft zones jiggle after a slap (needs the limb push slider above 0).");
            ReactSound = Config.Bind("Reactions", "Sound", true, "A slap sound at the spot of a slap.");
            ReactVolume = Config.Bind("Reactions", "Volume", 0.6f, new ConfigDescription("Slap sound volume.", new AcceptableValueRange<float>(0f, 1f)));
            ReactSkinVolume = Config.Bind("Reactions", "SkinVolume", 0.8f, new ConfigDescription("Volume of the pat, grab and rub sounds (0.11.7: was the slap volume).", new AcceptableValueRange<float>(0f, 1f)));
            ReactFoley = Config.Bind("Reactions", "Foley", true, "Skin sounds: slaps from your own WAV files in BepInEx\\plugins\\SfMorph\\Sounds (slap_soft / slap_medium / slap_hard; none: the game's clips), a pat on light taps, a grab when a hand closes on her, a rub while a hand slides over her skin (your files in pat / grab / rub, else built-in sounds). Off: the game's slap clips only.");
            ReactRubSound = Config.Bind("Reactions", "RubSound", true, "With Skin sounds on: the soft rub while a hand slides over her skin (your files in Sounds\\rub, else a built one). Off mutes just the rub; slaps, pats and grabs keep sounding.");
            ReactPenSound = Config.Bind("Reactions", "PenetrationSound", true, "With Skin sounds on: a sound when a finger (your hand models, the physics hands, the embodied partner's hands) goes into her vagina, anus or mouth, and on each in / out stroke (your files in Sounds\\penetration; an empty folder is silent). Louder with speed, at most a few per second.");
            ReactPenVolume = Config.Bind("Reactions", "PenetrationVolume", 0.8f, new ConfigDescription("Volume of the penetration sound (times the skin volume).", new AcceptableValueRange<float>(0f, 1f)));
            ReactPenReact = Config.Bind("Reactions", "PenetrationReacts", true, "A finger going into her vagina or anus excites her like a grope (excitement, voice, lines for the area, face, the game's desire), more on each in-stroke; in her mouth a little. Off: only the sound.");
            VoicePackName = Config.Bind("Voice", "Pack", "", "Her reaction voice from a voice pack: a folder name under BepInEx\\plugins\\SfMorph\\Sounds\\voice (breath_calm, breath_heavy, moan_low, moan_mid, moan_high, climax, exertion with WAV files). Empty: the game's own voice. While a pack is on, the game's lines and feeling sounds are not used (another voice).");
            VoiceMuteGame = Config.Bind("Voice", "MuteGameVoice", false, "Unused since 0.11.35 (a pack always replaces her game voice where it has a fitting clip). Was: While a voice pack is on: the game's own moans, sex voice and dirty talk are silent, and its moans in sex are answered by the pack's (the pack's clips still play on the same sources).");
            VoiceNonEnglish = Config.Bind("Voice", "NonEnglish", false, "A pack with lines in another language (its lang\\<Language> folder: Mariana Spanish, Aria51 Japanese, hummingael French, from_your_mouth Russian) speaks those instead of its English lines; moans and breaths are the same.");
            VoicePackInScenes = Config.Bind("Voice", "PackInScenes", true, "With a pack: the game's climax / creampie / condom / clean / finish / tease scenes (Timelines whose voice the mute silences) are filled with the pack's clips: request lines with climax sounds and high moans, invite lines, afterglow lines and breaths.");
            VoiceMuteDialogue = Config.Bind("Voice", "MuteGameDialogue", true, "Unused since 0.11.35 (the dialogue source plays nothing in this game build). Was: With MuteGameVoice: the game's dialogue voice is silent too (it also talks through it in sex scenes). Off = story dialogue stays audible.");
            VoicePitch = Config.Bind("Voice", "Pitch", 1f, new ConfigDescription("Her voice's pitch (moans, lines, dialogue), without changing its speed. 1 = as recorded.", new AcceptableValueRange<float>(0.75f, 1.4f)));
            VoiceFormant = Config.Bind("Voice", "Formant", 1f, new ConfigDescription("Her voice's formants (the size of the voice: below 1 fuller / older, above 1 smaller / younger), without changing pitch or speed. 1 = as recorded.", new AcceptableValueRange<float>(0.8f, 1.25f)));
            LipSyncOn = Config.Bind("Voice", "LipSync", true, "Her mouth follows Audio2Face lip-sync curves baked for her voice clips (tools/a2f; curves in BepInEx\\config\\SfMorphs\\lipsync\\<pack> mirroring the pack's folders, the game's clips in lipsync\\game), through the mouth poses in arkit-map.txt. A clip without a curve keeps the loudness jaw / the game's mouth. Needs Reactions, Face and Face clips on.");
            LipStrength = Config.Bind("Voice", "LipStrength", 1f, new ConfigDescription("How strongly the curves move her mouth (1 = as baked).", new AcceptableValueRange<float>(0f, 1.5f)));
            // 0.11.12: replaces LipLead (its range could not move the mouth later, and a maxed value put it ~0.3 s early); the audio output's own
            // buffer latency is now added by the plugin, this is only the rest (the headset link)
            LipLead = Config.Bind("Voice", "LipTiming", 0f, new ConfigDescription("Seconds the mouth moves ahead of the sound (+ = earlier, - = later). The game's audio buffer delay is already accounted for; this is for the rest (wireless headset audio).", new AcceptableValueRange<float>(-0.3f, 0.3f)));
            LipOverGame = Config.Bind("Voice", "LipOverGame", 0.5f, new ConfigDescription("Under the lip-sync poses: 0 = the game's mouth of the moment (the poses add on top), 1 = her neutral mouth (the game's mouth is replaced).", new AcceptableValueRange<float>(0f, 1f)));
            LipMood = Config.Bind("Voice", "LipMood", 0.7f, new ConfigDescription("While a lip-synced clip plays, how much of her expression's mouth (the face clip of her mood: a smile, a grimace) stays under the lip-sync poses. 0 = the poses go on the base above (the expression keeps only its eyes, brows and cheeks), 1 = the expression's full mouth under them.", new AcceptableValueRange<float>(0f, 1f)));
            LipBrows = Config.Bind("Voice", "LipBrows", 0.7f, new ConfigDescription("How much the lip-sync curves move her brows while she speaks (inner / outer raise, lowerer), on top of her expression's brows. 0 = the expression's brows only.", new AcceptableValueRange<float>(0f, 1.5f)));
            LipSticky = Config.Bind("Voice", "LipSticky", 0.6f, new ConfigDescription("Sticky lips: when her mouth starts to open, the lips cling together for a moment before they part. 0 = off.", new AcceptableValueRange<float>(0f, 1f)));
            ReactBlush = Config.Bind("Reactions", "Blush", true, "Her face reddens with excitement and after slaps.");
            ReactBlushBody = Config.Bind("Reactions", "BlushBody", false, "Her body skin reddens too, at half strength.");
            ReactBlushGain = Config.Bind("Reactions", "BlushGain", 0.6f, new ConfigDescription("How strong the redness is.", new AcceptableValueRange<float>(0f, 2f)));
            ReactGaze = Config.Bind("Reactions", "Gaze", false, "She looks away for a moment after a hard hit.");
            ReactFeedDesire = Config.Bind("Reactions", "FeedDesire", true, "Touch also raises the game's own desire gauge, which drives its escalating face (at most every 3 s, in sex or foreplay mode).");
            ReactConfigRev = Config.Bind("Reactions", "ConfigRev", 0, new ConfigDescription("Internal: one-time config migrations already applied. Do not change.", new AcceptableValueRange<int>(0, 100), "Advanced"));
            ReactAuto = Config.Bind("Reactions", "AutoState", true, "States (aroused, startled, guarded, afterglow) change by themselves.");
            ReactClimax = Config.Bind("Reactions", "AllowClimax", false, "At full excitement she climaxes (cosmetic: three moans, then afterglow).");
            VoiceGuard = Config.Bind("Reactions", "VoiceGuard", true, "Repair the game's voice shuffle errors (IndexOutOfRange / NullReference in PlayRandomVoice, for dirty talk and the sex sound effects) by rebuilding the stale shuffle. Works without Reactions.");
            ReactLog = Config.Bind("Reactions", "Log", 1, new ConfigDescription("0 quiet, 1 the first 20 events then every 10th, 2 every event.", new AcceptableValueRange<int>(0, 2)));
            // one-time migration: FeedDesire used to default to false; the game's own escalating face follows its desire gauge, so it is switched on once
            if (ReactConfigRev.Value < 1)
            {
                bool wasOff = !ReactFeedDesire.Value;
                ReactFeedDesire.Value = true; ReactConfigRev.Value = 1;
                if (wasOff) Logger.LogInfo("Config migration: [Reactions] FeedDesire turned on (revision 1)");
            }
            // one-time migration (0.9.4): the ripple was too small and too quick, and the belly fold read as a scar; values still at the old
            // defaults move to the new ones (a value the user chose is kept)
            if (ReactConfigRev.Value < 2)
            {
                var moved = new List<string>();
                Action<ConfigEntry<float>, float, float> mv = (e, was, now) => { if (Mathf.Abs(e.Value - was) < 1e-4f) { e.Value = now; moved.Add(e.Definition.Key + " " + now); } };
                mv(RippleAmount, 0.6f, 1f); mv(RippleSpeed, 1f, 0.5f); mv(RippleWavelength, 0.07f, 0.1f); mv(RippleDamping, 0.3f, 0.6f); mv(BendBellyFoldMm, 5f, 3f);
                ReactConfigRev.Value = 2;
                if (moved.Count > 0) Logger.LogInfo("Config migration: [Physics] " + string.Join(", ", moved.ToArray()) + " (revision 2)");
            }
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
                Perf.On = _p.PerfLog.Value;
                if (!Perf.LoopOk) Perf.Roll();   // without the phase markers the frame is rolled here
                if (Keys.Pressed(_p.PanelKey.Value, _p.KeysUnfocused.Value)) _panel.Visible = !_panel.Visible;
                _body.Tick();
                Avatar.Tick(_body);
                if (Time.unscaledTime >= _nextLoopCheck)
                {
                    _nextLoopCheck = Time.unscaledTime + 2f;
                    bool was = _inLoop;
                    _inLoop = ApplyStep.Ensure(Apply);
                    Perf.EnsureLoop();
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
            if (!Perf.LoopOk) Perf.Step();
            Perf.Begin(Perf.S.HandsFixed);
            try { PhysicsHands.FixedStep(); }
            catch (Exception e) { Plugin.Log.LogError("Physics hands failed, turned off: " + e); _p.HandsOn.Value = false; try { PhysicsHands.Release(); } catch { } }
            Perf.End(Perf.S.HandsFixed);
        }

        // Fallback only: in LateUpdate the game's physics may already have run for this frame.
        void LateUpdate() { if (!_inLoop) Apply(); }

        void Apply()
        {
            if (_broken) return;
            Perf.Begin(Perf.S.Total);
            try { _body.ApplyAll(); }
            catch (Exception e) { Fail(e); }
            Perf.End(Perf.S.Total);
        }

        void OnGUI()
        {
            if (_broken) return;
            Perf.Begin(Perf.S.Panel);
            try { _panel.Draw(); }
            catch (Exception e) { Fail(e); }
            Perf.End(Perf.S.Panel);
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
