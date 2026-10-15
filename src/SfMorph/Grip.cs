namespace SfMorph
{
    // The VR controllers' grip (0..1), read once per frame in the apply step, shared by the physics hands and Embody.
    // (Interface stub for 0.7.2.)
    internal static class Grip
    {
        public static float Left, Right;
        public static string Source = "";
        public static void Update() { }
    }
}
