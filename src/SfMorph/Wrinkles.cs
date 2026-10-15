using UnityEngine;

namespace SfMorph
{
    internal class WrinkleSettings
    {
        public bool On, Face = true, Neck = true, BodyOn;
        public float Strength = 1f, Threshold = 0.025f;
        public int Debug;             // 0 off, 1 activation, 2 axis
        public bool Recapture;        // one-shot (panel button)
        public float Attack = 0.12f, Release = 0.3f, SpacingFace = 0.004f, SpacingBody = 0.008f, CullDistance = 3f;
        public int Resolution;        // 0 base size (cap 4096), else 2048/1024
    }

    // Procedural skin wrinkles (0.7.4): compression strain on the GPU -> folds rasterised in UV space -> blended into her skin's
    // normal map. (Interface stub.)
    internal class Wrinkles
    {
        public string Status = "";
        public static Wrinkles TryLoad(string dir) { return null; }
        public void Update(Character her, WrinkleSettings s) { }
        public void Release() { Status = ""; }
        public static bool Owns(Texture t) { return false; }
    }
}
