using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // One character's changes. Everything is relative to what the game does:
    // shapes are added to the game's weight, bone and region sizes multiply the
    // game's scale. So the game's own customisation and animated shapes (face,
    // lip sync) keep working underneath.
    internal class Look
    {
        public readonly Dictionary<string, float> Shapes = new Dictionary<string, float>();   // shape name -> added weight
        public readonly Dictionary<string, Vector3> Bones = new Dictionary<string, Vector3>(); // bone name -> scale factor
        public readonly Dictionary<string, float> Regions = new Dictionary<string, float>();   // region id -> size factor

        public bool Empty { get { return Shapes.Count == 0 && Bones.Count == 0 && Regions.Count == 0; } }
        public int Count { get { return Shapes.Count + Bones.Count + Regions.Count; } }

        public void Clear() { Shapes.Clear(); Bones.Clear(); Regions.Clear(); }

        public void CopyFrom(Look o)
        {
            Clear();
            foreach (var kv in o.Shapes) Shapes[kv.Key] = kv.Value;
            foreach (var kv in o.Bones) Bones[kv.Key] = kv.Value;
            foreach (var kv in o.Regions) Regions[kv.Key] = kv.Value;
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Plain text, one change per line: kind<TAB>name<TAB>value.
        public string Write()
        {
            var sb = new StringBuilder("# SF Body Morphs look\n");
            foreach (var kv in Regions) sb.Append("region\t").Append(kv.Key).Append('\t').Append(kv.Value.ToString("0.###", Inv)).Append('\n');
            foreach (var kv in Shapes) sb.Append("shape\t").Append(kv.Key).Append('\t').Append(kv.Value.ToString("0.##", Inv)).Append('\n');
            foreach (var kv in Bones)
                sb.Append("bone\t").Append(kv.Key).Append('\t').Append(kv.Value.x.ToString("0.###", Inv)).Append(' ')
                  .Append(kv.Value.y.ToString("0.###", Inv)).Append(' ').Append(kv.Value.z.ToString("0.###", Inv)).Append('\n');
            return sb.ToString();
        }

        public void Read(string text)
        {
            Clear();
            foreach (var raw in text.Split('\n'))
            {
                var f = raw.TrimEnd('\r').Split('\t');
                if (f.Length < 3 || f[0].StartsWith("#")) continue;
                float v;
                switch (f[0])
                {
                    case "region": if (float.TryParse(f[2], NumberStyles.Float, Inv, out v)) Regions[f[1]] = v; break;
                    case "shape": if (float.TryParse(f[2], NumberStyles.Float, Inv, out v)) Shapes[f[1]] = v; break;
                    case "bone":
                        var p = f[2].Split(' ');
                        float x, y, z;
                        if (p.Length == 3 && float.TryParse(p[0], NumberStyles.Float, Inv, out x) && float.TryParse(p[1], NumberStyles.Float, Inv, out y)
                            && float.TryParse(p[2], NumberStyles.Float, Inv, out z)) Bones[f[1]] = new Vector3(x, y, z);
                        break;
                }
            }
        }
    }

    // BepInEx\config\SfMorphs\characters\<name>.txt is each character's look,
    // saved as you edit and loaded when that character appears;
    // ...\presets\<name>.txt are looks saved by name, to load onto anyone.
    internal static class Store
    {
        public static string Dir { get { return Path.Combine(BepInEx.Paths.ConfigPath, "SfMorphs"); } }
        static string CharDir { get { return Path.Combine(Dir, "characters"); } }
        static string PresetDir { get { return Path.Combine(Dir, "presets"); } }

        public static string Safe(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString().Trim();
        }

        public static bool LoadCharacter(string key, Look look) { return Load(Path.Combine(CharDir, Safe(key) + ".txt"), look); }

        public static void SaveCharacter(string key, Look look)
        {
            var path = Path.Combine(CharDir, Safe(key) + ".txt");
            if (look.Empty) { if (File.Exists(path)) File.Delete(path); return; }
            Directory.CreateDirectory(CharDir);
            File.WriteAllText(path, look.Write());
        }

        public static List<string> Presets()
        {
            var list = new List<string>();
            if (Directory.Exists(PresetDir))
                foreach (var f in Directory.GetFiles(PresetDir, "*.txt")) list.Add(Path.GetFileNameWithoutExtension(f));
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        public static bool LoadPreset(string name, Look look) { return Load(Path.Combine(PresetDir, Safe(name) + ".txt"), look); }

        public static void SavePreset(string name, Look look)
        {
            Directory.CreateDirectory(PresetDir);
            File.WriteAllText(Path.Combine(PresetDir, Safe(name) + ".txt"), look.Write());
        }

        static bool Load(string path, Look look)
        {
            if (!File.Exists(path)) return false;
            try { look.Read(File.ReadAllText(path)); return true; }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't read " + path + ": " + e.Message); return false; }
        }
    }
}
