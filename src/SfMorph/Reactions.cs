using UnityEngine;

namespace SfMorph
{
    // Her reactions to touch (0.7.9): what the Body hands the director each frame. Filled from the config once per frame (no allocation).
    internal class ReactionSettings
    {
        public bool On, Voice, Face, Flinch, Sound, Blush, BlushBody, Gaze, FeedDesire, AutoState, AllowClimax, VoiceGuard;
        public float Sensitivity, Cooldown, Volume, BlushGain, VoicePitchVar;
        public int VoiceMode, Log;

        public void Fill(Plugin p)
        {
            On = p.ReactOn.Value; Voice = p.ReactVoice.Value; Face = p.ReactFace.Value; Flinch = p.ReactFlinch.Value; Sound = p.ReactSound.Value;
            Blush = p.ReactBlush.Value; BlushBody = p.ReactBlushBody.Value; Gaze = p.ReactGaze.Value; FeedDesire = p.ReactFeedDesire.Value;
            AutoState = p.ReactAuto.Value; AllowClimax = p.ReactClimax.Value; VoiceGuard = p.VoiceGuard.Value;
            Sensitivity = p.ReactSensitivity.Value; Cooldown = p.ReactCooldown.Value; Volume = p.ReactVolume.Value;
            BlushGain = p.ReactBlushGain.Value; VoicePitchVar = p.ReactVoicePitchVar.Value;
            VoiceMode = p.ReactVoiceMode.Value; Log = p.ReactLog.Value;
        }
    }

    internal enum RState { Idle, Aroused, Startled, Guarded, Climax, Afterglow }

    // The reactions director (0.7.9): classifies touches (caress, grope, slaps), keeps her excitement and pain, and drives the responses
    // (voice, face, flinch, slap sound, blush, gaze) through ReactionsGame. R0: the interface only; Update does nothing yet.
    internal sealed class Reactions
    {
        public string Status = "", StateLine = "", Bars = "", GuardStatus = "";
        public float E, P;
        public int Tier;
        public RState State;

        public static readonly string[] ZoneNames = { "Breast", "Chest", "Belly", "Hips", "Butt", "Thigh", "Calf", "Arm", "Face", "Neck" };

        // Runs in Body.ApplyAll for her, after the physics hands and before her soft zones simulate.
        public void Update(Character her, ReactionSettings s, float dt, Overlap overlap)
        {
            Status = "off: not implemented yet";
        }

        // Gives back everything we changed (blush, look-at weight, the guard's hooks) and forgets the session.
        public void Release()
        {
            Status = ""; StateLine = ""; Bars = ""; GuardStatus = "";
            E = 0f; P = 0f; Tier = 0; State = RState.Idle;
        }

        // The panel's state buttons: jump to a state with the given excitement and pain.
        public void SetState(RState st, float e, float p) { }

        // The panel's test buttons: a reaction of `tier` (1 caress .. 5 too hard) on `zone` (ZoneNames) at side 0 left / 1 right.
        public void Test(int zone, int side, int tier) { }

        // 0 moan, 1 breath, 2 wince, 3 slap sound, 4 flinch butt
        public void TestResponse(int which) { }
    }
}
