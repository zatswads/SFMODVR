using System;
using System.Collections.Generic;
using UnityEngine;

namespace SfMorph
{
    // 0.8.0 hand prints and slap redness (design 3): GpuContact's per-vertex _Marks (x redness, y live pressure, z lingering dent) rasterised
    // into a UV marks texture per skin material, composed into a copy of her albedo (redness, pressed-flesh darkening) and normal map.
    internal sealed class SkinMarks
    {
        public string Status = "";
        // True while something is fading or pressed (GpuContact keeps the marks decay running while this or its own pressure is live).
        public static bool Active;

        // A slap mark at a world point (Reactions, tier >= 3): queued, GpuContact's MarkDisc kernel adds it before Compose.
        public static void Slap(Vector3 worldPoint, float radius, float intensity) { }

        // GpuContact.Run: the queued discs since the last call (xyz world centre, w radius; amt = added redness 0..1). Returns the count.
        public static int TakeDiscs(Vector4[] disc, float[] amt) { return 0; }

        // Main thread, every frame after GpuStep (Body.ApplyAll).
        public void Update(Character her, ContactSettings s, GpuContact gpu, float now) { }

        public void Release() { Status = ""; Active = false; }
    }
}
