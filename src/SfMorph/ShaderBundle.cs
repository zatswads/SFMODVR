using UnityEngine;

namespace SfMorph
{
    // The shared shader bundle (dist/sfmorph_shaders), ref-counted between GpuContact and Wrinkles. (Interface stub.)
    internal static class ShaderBundle
    {
        public static bool Acquire(string dir, out string error) { error = "stub"; return false; }
        public static T Load<T>(string name) where T : Object { return null; }
        public static void Release() { }
    }
}
