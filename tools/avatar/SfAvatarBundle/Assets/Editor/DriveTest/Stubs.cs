// Stand-ins for the plugin types src/SfMorph/Avatar.cs uses, so tools/avatar/drive_test.ps1 can compile that file unchanged in
// this editor project and run it on her export + a pose recording (AvatarDriveTest). Not part of the plugin.
using System.Collections.Generic;
using UnityEngine;

namespace BepInEx.Configuration
{
    public class ConfigDescription
    {
        public ConfigDescription(string d, object range = null) { }
    }
    public class AcceptableValueRange<T>
    {
        public AcceptableValueRange(T min, T max) { }
    }
    public class ConfigEntry<T>
    {
        public T Value;
    }
    public class ConfigFile
    {
        public ConfigEntry<T> Bind<T>(string section, string key, T def, string desc) { return new ConfigEntry<T> { Value = def }; }
        public ConfigEntry<T> Bind<T>(string section, string key, T def, ConfigDescription desc) { return new ConfigEntry<T> { Value = def }; }
    }
}

namespace SfMorph
{
    internal class Logger
    {
        public void LogInfo(object o) { Debug.Log("SfAvatarTest log: " + o); }
        public void LogWarning(object o) { Debug.LogWarning("SfAvatarTest warn: " + o); }
        public void LogError(object o) { Debug.LogError("SfAvatarTest error: " + o); }
    }
    internal static class Plugin { public static Logger Log = new Logger(); }
    internal static class Store { public static string Dir = ""; }
    internal class LookStub { public string Morph; }
    internal class Character
    {
        public Transform Root;
        public Dictionary<string, Transform> Bones = new Dictionary<string, Transform>();   // PoseDriver (0.11.48 auto clavicle)
        public Dictionary<Transform, Matrix4x4> Bind = new Dictionary<Transform, Matrix4x4>();
        public bool Female = true, IsPlayer;
        public LookStub Look = new LookStub();
    }
    internal class Body { public readonly List<Character> Found = new List<Character>(); }
    internal static class VrHands
    {
        internal static bool UnderSf(Transform t)
        {
            for (var p = t; p != null; p = p.parent) if (p.name.StartsWith("Sf", System.StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
