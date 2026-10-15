using System.Collections.Generic;
using UnityEngine;

namespace SfMorph
{
    // Embody-like VR for the partner: view at his eyes, his head hidden, his hands following the VR controllers.
    // (Interface stub: implemented in 0.7.0.)
    internal class EmbodySettings
    {
        public bool On;
        public int Hands;          // 0 none, 1 left, 2 right, 3 both
        public bool HideHead;
        public int View;           // 0 the game's camera, 1 snap once to his eyes, 2 follow his eyes (position only)
        public int Partner;        // 0 MovePlayerAndGirl.player, 1 .badGuy
        public Vector3 LeftPos, LeftRot, RightPos, RightRot;   // controller -> hand offsets (m, degrees)
    }

    internal class Embody
    {
        public string Status = "";
        public void Update(Character her, List<Character> everyone, EmbodySettings s) { }   // every frame in the apply step, after Body applies
        public void SnapView() { }                                                          // panel button
        public void Release() { Status = ""; }
    }
}
