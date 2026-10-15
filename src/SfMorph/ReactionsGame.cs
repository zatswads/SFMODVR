using UnityEngine;

namespace SfMorph
{
    // The reflection bridge to the game's facial, voice, sound, sex-mode and material members for Reactions (0.7.9), and the dirty-talk
    // voice guard. R0: no-op stubs.
    internal sealed class ReactionsGame
    {
#pragma warning disable 649   // filled by package A
        public bool Found;
        public string Missing = "";
#pragma warning restore 649

        public bool Refresh(Character her, float now) { return false; }
        // InSexMode, VoiceOn, SfxOn, DirtyTalkPlaying, DialoguePlaying, DirtyTalkPause, FunctionPause, FacialLayer, MoanPlaying, TouchReactionTime, Desire
        public void ReadGates(float now) { }
        public bool Moan() { return false; }
        public bool MoanClip(int bank, float pitch) { return false; }
        public bool Breath() { return false; }
        public bool WinceTrigger() { return false; }
        public bool Blink() { return false; }
        public void ResetMoan() { }
        public bool Slap(Vector3 at, float vol, float pitch) { return false; }
        public void SetBlush(float face, float body) { }
        public void SetGaze(float away) { }
        public void FeedDesire() { }
        public void Guard(bool on, float now) { }
        public void Release() { }
    }
}
