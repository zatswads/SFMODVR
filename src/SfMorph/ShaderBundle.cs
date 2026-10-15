using System.IO;
using UnityEngine;

namespace SfMorph
{
    // The shared shader bundle (sfmorph_shaders next to the DLL), ref-counted between GpuContact and Wrinkles: the first Acquire loads
    // it, the last Release unloads it (Unity refuses to load the same bundle twice).
    internal static class ShaderBundle
    {
        static AssetBundle _bundle;
        static int _refs;

        public static bool Acquire(string dir, out string error)
        {
            error = null;
            bool held = _refs > 0;   // holders exist: never reset the count, only (re)load a bundle that went missing
            if (held && _bundle != null) { _refs++; return true; }
            string path = Path.Combine(dir ?? "", "sfmorph_shaders");
            if (!File.Exists(path)) { error = "not found: " + path; return false; }
            var b = AssetBundle.LoadFromFile(path);
            if (b == null) { error = "could not load " + path; return false; }
            _bundle = b;
            _refs = held ? _refs + 1 : 1;
            return true;
        }

        public static T Load<T>(string name) where T : Object
        {
            return _bundle != null ? _bundle.LoadAsset<T>(name) : null;
        }

        public static void Release()
        {
            if (_refs <= 0) return;
            if (--_refs > 0) return;
            if (_bundle != null) { _bundle.Unload(true); _bundle = null; }
        }
    }
}
