using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // One character's changes. Everything is relative to what the game does
    // (sizes multiply the game's scale, moves add to its position), so the game's
    // animation, physics and customisation keep working underneath.
    internal class Look
    {
        public readonly Dictionary<string, Vector3> Bones = new Dictionary<string, Vector3>(); // bone name -> scale factor
        public readonly Dictionary<string, float> Regions = new Dictionary<string, float>();   // slider (Detail) id -> value

        // A converted character from the Characters tab (folder name under SfMorphs\morphs), how much of its face and
        // body shape, and whether its joints and baked skin are used. (Characters without head weights have one shape,
        // driven by MorphFace.)
        public string Morph;
        public float MorphFace = 1f, MorphBody = 1f;
        public bool MorphJoints = true, MorphSkin = true;

        public bool Empty { get { return Bones.Count == 0 && Regions.Count == 0 && Morph == null; } }
        public int Count { get { return Bones.Count + Regions.Count + (Morph != null ? 1 : 0); } }

        public void Clear() { Bones.Clear(); Regions.Clear(); Morph = null; MorphFace = 1f; MorphBody = 1f; MorphJoints = true; MorphSkin = true; }

        public void CopyFrom(Look o)
        {
            Clear();
            Morph = o.Morph; MorphFace = o.MorphFace; MorphBody = o.MorphBody; MorphJoints = o.MorphJoints; MorphSkin = o.MorphSkin;
            foreach (var kv in o.Bones) Bones[kv.Key] = kv.Value;
            foreach (var kv in o.Regions) Regions[kv.Key] = kv.Value;
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Plain text, one change per line: kind<TAB>name<TAB>value.
        public string Write()
        {
            var sb = new StringBuilder("# SF Body Morphs look\n");
            if (Morph != null)
                sb.Append("character\t").Append(Morph).Append('\t').Append(MorphFace.ToString("0.###", Inv)).Append(' ').Append(MorphBody.ToString("0.###", Inv)).Append(' ')
                  .Append(MorphJoints ? 1 : 0).Append(' ').Append(MorphSkin ? 1 : 0).Append('\n');
            foreach (var kv in Regions) sb.Append("region\t").Append(kv.Key).Append('\t').Append(kv.Value.ToString("0.###", Inv)).Append('\n');
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
                    case "character":
                        var c = f[2].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        Morph = f[1];
                        if (c.Length >= 4)   // new: face body joints skin
                        {
                            if (float.TryParse(c[0], NumberStyles.Float, Inv, out v)) MorphFace = v;
                            if (float.TryParse(c[1], NumberStyles.Float, Inv, out v)) MorphBody = v;
                            MorphJoints = c[2] != "0";
                            MorphSkin = c[3] != "0";
                        }
                        else   // old: weight joints skin (one weight for both)
                        {
                            if (c.Length > 0 && float.TryParse(c[0], NumberStyles.Float, Inv, out v)) { MorphFace = v; MorphBody = v; }
                            if (c.Length > 1) MorphJoints = c[1] != "0";
                            if (c.Length > 2) MorphSkin = c[2] != "0";
                        }
                        break;
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
