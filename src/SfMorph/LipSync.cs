using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SfMorph
{
    // 0.9.9 lip sync from offline Audio2Face-3D curves (tools/a2f, design: research-audio2face.md in the session notes).
    //
    // Curves: one CSV per clip (t + 52 ARKit + 16 tongue weights, 60 fps) baked from the same WAV the voice pack plays, under
    // BepInEx\config\SfMorphs\lipsync\<pack>\<the pack's relative path>.csv; the game's own clips (dumped by the Reactions tab button as
    // <clip name>_<samples>.wav) under lipsync\game\...\<clip name>_<samples>.csv, found by file name. A curve is read on first use, kept as
    // 16-bit weights of the mouth shapes only (Shapes), at most 160 in memory.
    //
    // Map: per ARKit shape at weight 1, a local delta per mouth bone (position in the parent's local units, rotation vector in degrees in
    // the bone's own frame, q = base * exp(r)), in BepInEx\config\SfMorphs\arkit-map.txt. Linear: the pose is the base plus the weighted
    // sum of the deltas. Sculpted in the Reactions tab's mouth pose editor, seeded from her face clips survey (ReactionsFace.Seed).
    internal static class LipSync
    {
        // the ARKit shapes that move the mouth (eyes and brows stay with the face clips / the game)
        public static readonly string[] Shapes =
        {
            "jawOpen", "jawForward", "jawLeft", "jawRight", "mouthClose", "mouthFunnel", "mouthPucker", "mouthLeft", "mouthRight",
            "mouthSmileLeft", "mouthSmileRight", "mouthFrownLeft", "mouthFrownRight", "mouthDimpleLeft", "mouthDimpleRight",
            "mouthStretchLeft", "mouthStretchRight", "mouthRollLower", "mouthRollUpper", "mouthShrugLower", "mouthShrugUpper",
            "mouthPressLeft", "mouthPressRight", "mouthLowerDownLeft", "mouthLowerDownRight", "mouthUpperUpLeft", "mouthUpperUpRight",
            "cheekPuff", "cheekSquintLeft", "cheekSquintRight", "tongueOut",
        };
        public static readonly int K = Shapes.Length;
        public const int JawOpen = 0, MouthClose = 4;

        public static string Dir { get { return Path.Combine(Store.Dir, "lipsync"); } }
        public static string MapPath { get { return Path.Combine(Store.Dir, "arkit-map.txt"); } }
        public static string DumpDir { get { return Path.Combine(Store.Dir, "voice-dump"); } }

        public static int ShapeIndex(string n) { for (int i = 0; i < K; i++) if (string.Equals(Shapes[i], n, StringComparison.OrdinalIgnoreCase)) return i; return -1; }

        static void L(string m) { Plugin.Log.LogInfo("Lip sync: " + m); }

        // ---------------- curves ----------------

        internal sealed class Curve
        {
            public int Frames; public float Fps; public ushort[] W;   // Frames x K, 0..65535 = 0..1
            public float PeakJaw, Seconds;
            public string Path = "";

            // the K weights at t seconds (linear between frames, held at the ends)
            public void Sample(float t, float[] w)
            {
                float fi = Mathf.Clamp(t * Fps, 0f, Frames - 1);
                int i0 = (int)fi, i1 = Math.Min(i0 + 1, Frames - 1);
                float f = fi - i0, a = (1f - f) / 65535f, b = f / 65535f;
                int o0 = i0 * K, o1 = i1 * K;
                for (int k = 0; k < K; k++) w[k] = W[o0 + k] * a + W[o1 + k] * b;
            }
        }

        static readonly Dictionary<string, Curve> _cache = new Dictionary<string, Curve>(StringComparer.OrdinalIgnoreCase);
        static readonly LinkedList<string> _lru = new LinkedList<string>();
        static readonly HashSet<string> _missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, string> _gameIndex;   // file stem "<clip>_<samples>" -> csv path
        static string _countPack; static int _packCurves = -1;
        public static int Failures;

        // Forgets everything read (the panel's Rescan, a new bake).
        public static void Rescan() { _cache.Clear(); _lru.Clear(); _missing.Clear(); _gameIndex = null; _countPack = null; _packCurves = -1; Failures = 0; }

        // How many curve files the pack has (cached per pack name; -1 = no folder).
        public static int PackCurves(string pack)
        {
            if (string.IsNullOrEmpty(pack)) return -1;
            if (pack == _countPack) return _packCurves;
            _countPack = pack; _packCurves = -1;
            try { string d = Path.Combine(Dir, pack); if (Directory.Exists(d)) _packCurves = Directory.GetFiles(d, "*.csv", SearchOption.AllDirectories).Length; }
            catch (Exception) { }
            return _packCurves;
        }

        // The curve of a voice pack WAV (full path under VoicePack.Root\<pack>), null = none.
        public static Curve ForPack(string pack, string wav)
        {
            if (string.IsNullOrEmpty(pack) || string.IsNullOrEmpty(wav)) return null;
            string root = Path.Combine(VoicePack.Root, pack);
            string rel = wav.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? wav.Substring(root.Length).TrimStart('\\', '/') : Path.GetFileName(wav);
            return Get(Path.Combine(Path.Combine(Dir, pack), Path.ChangeExtension(rel, ".csv")));
        }

        // The curve of one of the game's clips by its dump name "<name>_<samples>", null = none.
        public static Curve ForGame(string name, int samples)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_gameIndex == null)
            {
                _gameIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string d = Path.Combine(Dir, "game");
                    if (Directory.Exists(d))
                        foreach (var f in Directory.GetFiles(d, "*.csv", SearchOption.AllDirectories)) _gameIndex[Path.GetFileNameWithoutExtension(f)] = f;
                    if (_gameIndex.Count > 0) L(_gameIndex.Count + " curves for the game's clips in " + d);
                }
                catch (Exception e) { L("could not list " + Path.Combine(Dir, "game") + ": " + e.Message); }
            }
            string p;
            return _gameIndex.TryGetValue(DumpName(name, samples), out p) ? Get(p) : null;
        }

        public static string DumpName(string clip, int samples) { return SafeName(clip) + "_" + samples; }

        public static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "clip";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c == ' ' ? '_' : c);
            return sb.ToString();
        }

        static Curve Get(string path)
        {
            Curve c;
            if (_cache.TryGetValue(path, out c)) { _lru.Remove(path); _lru.AddFirst(path); return c; }
            if (_missing.Contains(path)) return null;
            if (!File.Exists(path)) { _missing.Add(path); return null; }
            string why;
            c = Read(path, out why);
            if (c == null) { _missing.Add(path); Failures++; Plugin.Log.LogWarning("Lip sync: " + Path.GetFileName(path) + " unreadable (" + why + ")"); return null; }
            _cache[path] = c; _lru.AddFirst(path);
            while (_cache.Count > 160) { var last = _lru.Last; _cache.Remove(last.Value); _lru.RemoveLast(); }
            return c;
        }

        // CSV: header "t,<shape>,..." then one row per frame. Only the Shapes columns are kept; missing columns stay 0.
        static Curve Read(string path, out string why)
        {
            why = null;
            string text;
            try { text = File.ReadAllText(path); } catch (Exception e) { why = e.Message; return null; }
            int p = 0, n = text.Length;
            int eol = text.IndexOf('\n');
            if (eol < 0) { why = "no rows"; return null; }
            var head = text.Substring(0, eol).Trim().Split(',');
            if (head.Length < 2 || head[0].Trim() != "t") { why = "no 't' column"; return null; }
            var col = new int[head.Length];   // column -> shape index (-1 skip)
            int found = 0;
            for (int i = 0; i < head.Length; i++) { col[i] = i == 0 ? -1 : ShapeIndex(head[i].Trim()); if (col[i] >= 0) found++; }
            if (found == 0) { why = "none of the mouth shapes in the header"; return null; }
            p = eol + 1;
            var rows = new List<ushort[]>(512);
            float t0 = 0f, t1 = 0f;
            while (p < n)
            {
                // one row
                int c = 0; var row = new ushort[K]; bool any = false; float t = 0f;
                while (p < n && text[p] != '\n')
                {
                    float v = Num(text, ref p);
                    if (c == 0) t = v;
                    else if (c < col.Length && col[c] >= 0) row[col[c]] = (ushort)Mathf.RoundToInt(Mathf.Clamp01(v) * 65535f);
                    any = true;
                    if (p < n && text[p] == ',') p++;
                    else if (p < n && text[p] != '\n' && text[p] != '\r') p++;   // stray character
                    if (p < n && text[p] == '\r') p++;
                    c++;
                }
                p++;
                if (!any || c < 2) continue;
                if (rows.Count == 0) t0 = t;
                t1 = t;
                rows.Add(row);
            }
            if (rows.Count < 2) { why = "fewer than 2 rows"; return null; }
            var cv = new Curve { Frames = rows.Count, Path = path };
            cv.Fps = t1 > t0 ? (rows.Count - 1) / (t1 - t0) : 60f;
            cv.W = new ushort[rows.Count * K];
            for (int r = 0; r < rows.Count; r++) { Array.Copy(rows[r], 0, cv.W, r * K, K); cv.PeakJaw = Mathf.Max(cv.PeakJaw, rows[r][JawOpen] / 65535f); }
            cv.Seconds = (rows.Count - 1) / cv.Fps;
            return cv;
        }

        // a plain decimal number (sign, digits, point, exponent); the parser stops at the first other character
        static float Num(string s, ref int p)
        {
            int n = s.Length;
            while (p < n && s[p] == ' ') p++;
            bool neg = false;
            if (p < n && (s[p] == '-' || s[p] == '+')) { neg = s[p] == '-'; p++; }
            double v = 0; double scale = 1; bool frac = false;
            for (; p < n; p++)
            {
                char ch = s[p];
                if (ch >= '0' && ch <= '9') { if (frac) { scale *= 0.1; v += (ch - '0') * scale; } else v = v * 10 + (ch - '0'); }
                else if (ch == '.' && !frac) frac = true;
                else break;
            }
            if (p < n && (s[p] == 'e' || s[p] == 'E'))
            {
                p++;
                bool en = false; if (p < n && (s[p] == '-' || s[p] == '+')) { en = s[p] == '-'; p++; }
                int e = 0; for (; p < n && s[p] >= '0' && s[p] <= '9'; p++) e = e * 10 + (s[p] - '0');
                v *= Math.Pow(10, en ? -e : e);
            }
            return (float)(neg ? -v : v);
        }

        // ---------------- map ----------------

        internal sealed class Pose { public string Bone; public Vector3 P, R; }   // P local units, R degrees (rotation vector)

        // per shape the posed bones (by name, resolved by ReactionsFace)
        public static readonly List<Pose>[] Map = NewMap();
        public static int MapVersion;     // bumped on every change (ReactionsFace re-resolves)
        public static bool MapLoaded, Dirty;
        public static string MapStatus = "";
        public static string MapFileVersion = "";   // the plugin version in the file's header ("" = none / no file)

        static List<Pose>[] NewMap() { var m = new List<Pose>[Shapes.Length]; for (int i = 0; i < m.Length; i++) m[i] = new List<Pose>(); return m; }

        public static int PosedShapes { get { int n = 0; foreach (var l in Map) if (l.Count > 0) n++; return n; } }

        public static Pose Find(int s, string bone, bool add)
        {
            foreach (var e in Map[s]) if (e.Bone == bone) return e;
            if (!add) return null;
            var p = new Pose { Bone = bone };
            Map[s].Add(p);
            return p;
        }

        public static void Changed() { MapVersion++; Dirty = true; }

        // Reads arkit-map.txt once (or again from the panel). Lines: shape <tab> bone <tab> px py pz <tab> rx ry rz.
        public static void LoadMap(bool force)
        {
            if (MapLoaded && !force) return;
            MapLoaded = true;
            foreach (var l in Map) l.Clear();
            MapVersion++; Dirty = false;
            string path = MapPath;
            MapFileVersion = "";
            if (!File.Exists(path)) { MapStatus = "no " + path + " yet: build or sculpt the shapes, then Save"; return; }
            int n = 0, bad = 0;
            try
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (MapFileVersion.Length == 0 && line.StartsWith("# SF Body Morphs ")) { int c = line.IndexOf(':'); if (c > 17) MapFileVersion = line.Substring(17, c - 17).Trim(); }
                    if (line.Length == 0 || line[0] == '#') continue;
                    var f = line.Split('\t');
                    int s = f.Length >= 4 ? ShapeIndex(f[0].Trim()) : -1;
                    Vector3 pv, rv;
                    if (s < 0 || f[1].Trim().Length == 0 || !V3(f[2], out pv) || !V3(f[3], out rv)) { bad++; continue; }
                    var e = Find(s, f[1].Trim(), true);
                    e.P = pv; e.R = rv; n++;
                }
                MapStatus = n + " bone poses in " + PosedShapes + " shapes" + (bad > 0 ? ", " + bad + " unreadable lines skipped" : "");
                L("map: " + MapStatus + " (" + path + ")");
            }
            catch (Exception e) { MapStatus = "unreadable: " + e.Message; Plugin.Log.LogWarning("Lip sync: " + path + " unreadable: " + e.Message); }
        }

        public static bool SaveMap()
        {
            try
            {
                Directory.CreateDirectory(Store.Dir);
                var sb = new StringBuilder();
                sb.Append("# SF Body Morphs ").Append(Plugin.Version).Append(": ARKit mouth shape -> her mouth bones, one line per shape and bone at weight 1\n");
                sb.Append("# shape\tbone\tposition delta (parent's local units) x y z\trotation vector (degrees, the bone's own axes) x y z\n");
                var ci = CultureInfo.InvariantCulture;
                for (int s = 0; s < K; s++)
                    foreach (var e in Map[s])
                    {
                        if (e.P.sqrMagnitude < 1e-14f && e.R.sqrMagnitude < 1e-8f) continue;
                        sb.Append(Shapes[s]).Append('\t').Append(e.Bone).Append('\t')
                          .Append(e.P.x.ToString("0.#######", ci)).Append(' ').Append(e.P.y.ToString("0.#######", ci)).Append(' ').Append(e.P.z.ToString("0.#######", ci)).Append('\t')
                          .Append(e.R.x.ToString("0.###", ci)).Append(' ').Append(e.R.y.ToString("0.###", ci)).Append(' ').Append(e.R.z.ToString("0.###", ci)).Append('\n');
                    }
                File.WriteAllText(MapPath, sb.ToString());
                Dirty = false;
                MapStatus = "saved: " + PosedShapes + " shapes to " + MapPath;
                L(MapStatus);
                return true;
            }
            catch (Exception e) { MapStatus = "could not save: " + e.Message; Plugin.Log.LogWarning("Lip sync: " + MapStatus); return false; }
        }

        // a.b.c < d.e.f (missing or unreadable parts count as 0)
        public static bool VersionBefore(string a, string b)
        {
            var x = (a ?? "").Split('.'); var y = (b ?? "").Split('.');
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                int p = 0, q = 0;
                if (i < x.Length) int.TryParse(x[i], out p);
                if (i < y.Length) int.TryParse(y[i], out q);
                if (p != q) return p < q;
            }
            return false;
        }

        // 0.11.11: the clip-sampled poses of 0.9.9-0.11.10 (curled, pouting lower lip) are set aside as arkit-map-<version>.txt; the caller
        // builds new ones. True = moved.
        public static bool RetireOldMap(out string backup)
        {
            backup = "";
            try
            {
                if (!File.Exists(MapPath) || MapFileVersion.Length == 0 || !VersionBefore(MapFileVersion, "0.11.39")) return false;
                backup = Path.Combine(Store.Dir, "arkit-map-" + MapFileVersion + ".txt");
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(MapPath, backup);
                foreach (var l in Map) l.Clear();
                MapVersion++; Dirty = false; MapFileVersion = "";
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Lip sync: could not set the old map aside: " + e.Message); return false; }
        }

        static bool V3(string s, out Vector3 v)
        {
            v = Vector3.zero;
            var p = s.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            float x, y, z;
            if (p.Length != 3 || !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) || !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        // rotation vector (degrees) <-> quaternion
        public static Quaternion Exp(Vector3 rDeg)
        {
            float a = rDeg.magnitude;
            return a < 1e-5f ? Quaternion.identity : Quaternion.AngleAxis(a, rDeg / a);
        }

        public static Vector3 Log(Quaternion q)
        {
            if (q.w < 0f) { q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w; }
            float a; Vector3 ax;
            q.ToAngleAxis(out a, out ax);
            if (a > 180f) a -= 360f;
            return float.IsNaN(ax.x) || Mathf.Abs(a) < 1e-4f ? Vector3.zero : ax.normalized * a;
        }
    }
}
