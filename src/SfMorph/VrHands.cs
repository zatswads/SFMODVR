using System.Collections.Generic;
using UnityEngine;

namespace SfMorph
{
    // A contact capsule for the soft zones: endpoints in T's local space (so it follows T), radius in metres.
    // Kind: "vr" (the user's tracked hands/controllers), "partner" (Player/BadGuy hands), "thighL", "thighR", "forearm", "hand" (hers).
    internal struct SoftCapsule
    {
        public Transform T;
        public Vector3 LocalA, LocalB;
        public float Radius;
        public string Owner, Kind;
    }

    // Finds the hands that can touch her (the user's VR hands/controllers, the partner rigs' hands, her own forearms/hands/thighs)
    // and keeps them as SoftCapsules; logs a one-time rig survey. (Interface stub: implemented in 0.7.0.)
    internal static class VrHands
    {
        public static readonly List<SoftCapsule> Capsules = new List<SoftCapsule>();
        public static int Version;                               // bumped whenever Capsules changes
        public static Transform LeftController, RightController; // the tracked controller poses, null if not found
        public static string Status = "";

        public static void Refresh(Character her, List<Character> everyone) { }   // every 2 s while physics or embody is on
        public static void Survey(Character her) { }                              // once per session
        public static void AttachToGameBreasts(Transform her, bool on) { }        // DynamicBoneColliders for the game's breast physics
        public static void Release() { Capsules.Clear(); Version++; LeftController = RightController = null; Status = ""; }
    }
}
