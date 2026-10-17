using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace SfMorph
{
    // Skin-contact foley for the reactions (0.9.0): slaps in three strengths, a pat, a grab / squeeze and a skin rub loop. Clips come from WAV
    // files in BepInEx\plugins\SfMorph\Sounds\<category>\*.wav (the user's own sounds, never shipped with the plugin); a category without
    // files falls back: slaps to the next strength and then to the game's own slap clips (ReactionsGame), pat / grab / rub to sounds built here
    // from shaped noise (dry, short; nothing wet). AudioClip is reached by reflection (UnityEngine.AudioModule isn't referenced): the type is
    // the game's AudioSource.clip property type, handed in by ReactionsGame. Everything loads once, on the main thread, on first use.
    internal sealed class SkinFoley
    {
        // 0.11.25: penetration (a finger going into her vagina, anus or mouth, each in / out stroke): files only, no built fallback
        public const int SlapSoft = 0, SlapMedium = 1, SlapHard = 2, Pat = 3, Grab = 4, Rub = 5, Penetration = 6, NC = 7;
        public static readonly string[] Folders = { "slap_soft", "slap_medium", "slap_hard", "pat", "grab", "rub", "penetration" };

        sealed class Set
        {
            public readonly List<UnityEngine.Object> Clips = new List<UnityEngine.Object>();
            public readonly List<float> Len = new List<float>();
            public int Files;                  // clips from files (the rest were built here)
            public int[] Bag = new int[0];     // shuffle bag: every clip once before any repeats
            public int BagPos;
        }

        readonly Set[] _sets = new Set[NC];
        readonly System.Random _rng = new System.Random();
        MethodInfo _create, _setData;
        public bool Loaded, Failed;
        public string Summary = "";

        public static string Dir { get { return Path.Combine(Path.Combine(BepInEx.Paths.PluginPath, "SfMorph"), "Sounds"); } }

        public bool HasFiles(int c) { return Loaded && c >= 0 && c < NC && _sets[c] != null && _sets[c].Files > 0; }
        public bool Has(int c) { return Loaded && c >= 0 && c < NC && _sets[c] != null && _sets[c].Clips.Count > 0; }

        // Loads the files and builds the fallbacks. tClip = UnityEngine.AudioClip. Logs one summary line; never throws.
        public void Load(Type tClip)
        {
            if (Loaded || Failed) return;
            try
            {
                _create = tClip != null ? tClip.GetMethod("Create", new[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(bool) }) : null;
                _setData = tClip != null ? tClip.GetMethod("SetData", new[] { typeof(float[]), typeof(int) }) : null;
                if (_create == null || _setData == null) { Failed = true; Summary = "AudioClip.Create / SetData not found"; Plugin.Log.LogWarning("Skin foley: " + Summary); return; }
                for (int c = 0; c < NC; c++) _sets[c] = new Set();
                string dir = Dir;
                EnsureFolders(dir);
                try { foreach (var f in Folders) Directory.CreateDirectory(Path.Combine(dir, f)); } catch (Exception) { }   // a category added later (penetration)
                var sb = new System.Text.StringBuilder("Skin foley: ");
                int bad = 0;
                for (int c = 0; c < NC; c++)
                {
                    string d = Path.Combine(dir, Folders[c]);
                    if (!Directory.Exists(d)) continue;
                    var files = Directory.GetFiles(d, "*.wav");
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < files.Length && _sets[c].Clips.Count < 64; i++)
                    {
                        float[] s; int rate; string why;
                        if (!ReadWav(files[i], c == Rub ? 12f : 4f, c != Rub, out s, out rate, out why)) { bad++; if (bad <= 5) Plugin.Log.LogWarning("Skin foley: skipped " + Path.GetFileName(files[i]) + " (" + why + ")"); continue; }
                        if (Add(c, "SfFoley_" + Folders[c] + "_" + Path.GetFileNameWithoutExtension(files[i]), s, rate)) _sets[c].Files++;
                    }
                }
                // the built fallbacks: pat, grab, rub only (slaps fall back to the game's clips)
                if (_sets[Pat].Clips.Count == 0) for (int i = 0; i < 4; i++) Add(Pat, "SfFoley_pat_built" + i, BuildPat(i), 44100);
                if (_sets[Grab].Clips.Count == 0) for (int i = 0; i < 4; i++) Add(Grab, "SfFoley_grab_built" + i, BuildGrab(i), 44100);
                if (_sets[Rub].Clips.Count == 0) Add(Rub, "SfFoley_rub_built", BuildRub(), 44100);
                for (int c = 0; c < NC; c++)
                {
                    if (c > 0) sb.Append(", ");
                    var st = _sets[c];
                    sb.Append(Folders[c]).Append(' ');
                    if (st.Files > 0) sb.Append(st.Files).Append(st.Files == 1 ? " file" : " files");
                    else if (st.Clips.Count > 0) sb.Append("built");
                    else sb.Append(c <= SlapHard ? "none (next strength / the game's clips)" : c == Penetration ? "none (silent)" : "none");
                }
                if (bad > 0) sb.Append("; ").Append(bad).Append(" file(s) skipped");
                sb.Append(" (folder ").Append(dir).Append(')');
                Summary = sb.ToString();
                Plugin.Log.LogInfo(Summary);
                Loaded = true;
            }
            catch (Exception e) { Failed = true; Summary = "failed: " + e.Message; Plugin.Log.LogWarning("Skin foley failed: " + e); }
        }

        // A clip of category c (shuffle bag), or the next strength down / up for slaps; null = none (the caller falls back to the game's clips).
        public UnityEngine.Object Pick(int c, out float len)
        {
            len = 0f;
            if (!Loaded || c < 0 || c >= NC) return null;
            if (c <= SlapHard && _sets[c].Clips.Count == 0)
            {
                // soft -> medium -> hard; hard -> medium -> soft
                int[] order = c == SlapSoft ? new[] { SlapMedium, SlapHard } : c == SlapMedium ? new[] { SlapSoft, SlapHard } : new[] { SlapMedium, SlapSoft };
                foreach (int o in order) if (_sets[o].Clips.Count > 0) { c = o; break; }
            }
            var st = _sets[c];
            int n = st.Clips.Count;
            if (n == 0) return null;
            if (st.Bag.Length != n || st.BagPos >= n)
            {
                int last = st.Bag.Length == n && n > 1 ? st.Bag[n - 1] : -1;
                if (st.Bag.Length != n) { st.Bag = new int[n]; }
                for (int i = 0; i < n; i++) st.Bag[i] = i;
                for (int i = n - 1; i > 0; i--) { int j = _rng.Next(i + 1); int t = st.Bag[i]; st.Bag[i] = st.Bag[j]; st.Bag[j] = t; }
                if (n > 1 && st.Bag[0] == last) { int t = st.Bag[0]; st.Bag[0] = st.Bag[1]; st.Bag[1] = t; }   // no repeat across bags
                st.BagPos = 0;
            }
            int k = st.Bag[st.BagPos++];
            len = st.Len[k];
            return st.Clips[k];
        }

        bool Add(int c, string name, float[] s, int rate)
        {
            if (s == null || s.Length < 16) return false;
            var clip = _create.Invoke(null, new object[] { name, s.Length, 1, rate, false }) as UnityEngine.Object;
            if (clip == null) return false;
            _setData.Invoke(clip, new object[] { s, 0 });
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _sets[c].Clips.Add(clip); _sets[c].Len.Add(s.Length / (float)rate);
            return true;
        }

        // The folders and a short readme, so the user sees where the files go (only when the Sounds folder doesn't exist yet).
        static void EnsureFolders(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) return;
                Directory.CreateDirectory(dir);
                foreach (var f in Folders) Directory.CreateDirectory(Path.Combine(dir, f));
                File.WriteAllText(Path.Combine(dir, "README.txt"),
                    "SF Body Morphs skin foley: put WAV files (PCM 8/16/24/32-bit or float, mono or stereo, any rate) in these folders.\r\n" +
                    "slap_soft / slap_medium / slap_hard: slaps by strength. pat: light taps. grab: a hand closing on her. rub: a skin-on-skin\r\n" +
                    "stroke, played as a loop while a hand slides over her (seamless loops sound best). penetration: a finger going\r\n" +
                    "into her vagina, anus or mouth (each in / out stroke; no built sound, an empty folder is silent).\r\n" +
                    "Empty folders fall back: slaps to the next strength, then to the game's own slap clips; pat, grab and rub to built-in sounds.\r\n" +
                    "Read once per game start.\r\n");
            }
            catch (Exception) { }
        }

        // ---------------- WAV ----------------

        // RIFF/WAVE: PCM 8/16/24/32, IEEE float 32 (also WAVE_FORMAT_EXTENSIBLE), downmixed to mono; trimmed: leading silence below -50 dB,
        // trailing silence below -60 dB (plus 30 ms), capped at maxSec. trim false: the whole file (loops).
        internal static bool ReadWav(string path, float maxSec, bool trim, out float[] mono, out int rate, out string why)
        {
            int lead;
            return ReadWav(path, maxSec, trim, out mono, out rate, out lead, out why);
        }

        // lead: samples cut from the start by the trim (0.9.9: lip-sync curves are timed to the file as it is)
        internal static bool ReadWav(string path, float maxSec, bool trim, out float[] mono, out int rate, out int lead, out string why)
        {
            mono = null; rate = 0; why = null; lead = 0;
            var fi = new FileInfo(path);
            if (fi.Length > 32L * 1024 * 1024) { why = "over 32 MB"; return false; }
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E') { why = "not a RIFF/WAVE file"; return false; }
            int fmt = -1, ch = 0, bits = 0, dataOff = -1, dataLen = 0;
            int p = 12;
            while (p + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, p, 4);
                int len = BitConverter.ToInt32(b, p + 4);
                int body = p + 8;
                if (len < 0) break;
                if (id == "fmt " && body + 16 <= b.Length)
                {
                    fmt = BitConverter.ToUInt16(b, body); ch = BitConverter.ToUInt16(b, body + 2); rate = BitConverter.ToInt32(b, body + 4); bits = BitConverter.ToUInt16(b, body + 14);
                    if (fmt == 0xFFFE && len >= 40 && body + 26 <= b.Length) fmt = BitConverter.ToUInt16(b, body + 24);   // extensible: the sub-format GUID starts with the format tag
                }
                else if (id == "data") { dataOff = body; dataLen = Math.Min(len, b.Length - body); break; }
                p = body + len + (len & 1);
            }
            if (fmt < 0 || dataOff < 0) { why = "no fmt or data chunk"; return false; }
            if (ch < 1 || ch > 8 || rate < 8000 || rate > 192000) { why = "unsupported channels/rate " + ch + "/" + rate; return false; }
            bool flt = fmt == 3;
            if (!(fmt == 1 || flt) || (flt && bits != 32) || (!flt && bits != 8 && bits != 16 && bits != 24 && bits != 32)) { why = "unsupported format " + fmt + " / " + bits + " bit (convert to PCM WAV)"; return false; }
            int bps = bits / 8, frame = bps * ch;
            int n = dataLen / frame;
            int cap = (int)(maxSec * rate);
            if (n < 16) { why = "empty"; return false; }
            var s = new float[Math.Min(n, trim ? n : cap)];
            for (int i = 0; i < s.Length; i++)
            {
                float acc = 0f;
                int o = dataOff + i * frame;
                for (int c = 0; c < ch; c++, o += bps)
                {
                    float v;
                    if (flt) v = BitConverter.ToSingle(b, o);
                    else if (bits == 16) v = BitConverter.ToInt16(b, o) / 32768f;
                    else if (bits == 24) v = ((b[o] | (b[o + 1] << 8) | (b[o + 2] << 16)) << 8 >> 8) / 8388608f;
                    else if (bits == 32) v = BitConverter.ToInt32(b, o) / 2147483648f;
                    else v = (b[o] - 128) / 128f;
                    acc += v;
                }
                s[i] = acc / ch;
            }
            if (trim)
            {
                float pk = 0f;
                for (int i = 0; i < s.Length; i++) pk = Mathf.Max(pk, Mathf.Abs(s[i]));
                if (pk < 1e-4f) { why = "silent"; return false; }
                float onset = pk * 0.003f, tail = pk * 0.001f;
                int a = 0; while (a < s.Length && Mathf.Abs(s[a]) < onset) a++;
                a = Math.Max(0, a - rate / 1000);                 // 1 ms before the onset
                int e = s.Length - 1; while (e > a && Mathf.Abs(s[e]) < tail) e--;
                e = Math.Min(s.Length - 1, e + rate * 3 / 100);
                int m = Math.Min(e - a + 1, cap);
                if (m < 16) { why = "silent"; return false; }
                var t = new float[m];
                Array.Copy(s, a, t, 0, m);
                lead = a;
                int fade = Math.Min(m / 4, rate / 200);           // 5 ms fade-out
                for (int i = 0; i < fade; i++) t[m - 1 - i] *= i / (float)fade;
                s = t;
            }
            mono = s;
            return true;
        }

        // ---------------- built sounds (44.1 kHz mono) ----------------

        // first-order filters on white noise; seeded so every start sounds the same
        static float[] Noise(int n, int seed)
        {
            var r = new System.Random(seed);
            var s = new float[n];
            for (int i = 0; i < n; i++) s[i] = (float)(r.NextDouble() * 2.0 - 1.0);
            return s;
        }
        static void LowPass(float[] s, float hz) { float a = 1f - Mathf.Exp(-2f * Mathf.PI * hz / 44100f), y = 0f; for (int i = 0; i < s.Length; i++) { y += a * (s[i] - y); s[i] = y; } }
        static void HighPass(float[] s, float hz) { float a = 1f - Mathf.Exp(-2f * Mathf.PI * hz / 44100f), y = 0f; for (int i = 0; i < s.Length; i++) { y += a * (s[i] - y); s[i] -= y; } }
        static void Normalize(float[] s, float peak) { float pk = 1e-6f; foreach (var v in s) pk = Mathf.Max(pk, Mathf.Abs(v)); float g = peak / pk; for (int i = 0; i < s.Length; i++) s[i] *= g; }

        // A pat: a flat palm landing softly, a short broadband tap (2 ms attack, 25-40 ms decay) with a little low body.
        static float[] BuildPat(int v)
        {
            int n = 44100 * 9 / 100;
            var s = Noise(n, 11 + v);
            HighPass(s, 250f + 60f * v); LowPass(s, 3200f + 500f * v);
            var body = Noise(n, 101 + v); LowPass(body, 260f); LowPass(body, 260f);
            float dec = 0.025f + 0.005f * v;
            for (int i = 0; i < n; i++)
            {
                float t = i / 44100f;
                float env = t < 0.002f ? t / 0.002f : Mathf.Exp(-(t - 0.002f) / dec);
                s[i] = (s[i] + 2.5f * body[i] * Mathf.Exp(-t / 0.018f)) * env;
            }
            Normalize(s, 0.5f);
            return s;
        }

        // A grab / squeeze: fingers closing on skin. 0.11.11 (user: it sounded like hitting a piece of wood): 0.9.0 was low-passed noise
        // with an 8 ms attack and a 60 ms decay, which is a knock. A hand closing on skin has no transient: a breathy skin band
        // (250 Hz - 2.2 kHz, two poles each side) swelling in over 45-60 ms, held while the fingers settle (a slow 11-17 Hz waver), released
        // over ~150 ms, with a faint airy slide (2.5 - 6 kHz) and a soft low press under it, all on the same envelope. Sounds\grab first.
        static float[] BuildGrab(int v)
        {
            int n = 44100 * 40 / 100;
            var band = Noise(n, 31 + v); HighPass(band, 250f); HighPass(band, 250f); LowPass(band, 2200f - 150f * v); LowPass(band, 2200f - 150f * v);
            Normalize(band, 1f);
            var air = Noise(n, 61 + v); HighPass(air, 2500f); HighPass(air, 2500f); LowPass(air, 6000f);
            Normalize(air, 1f);
            var low = Noise(n, 81 + v); LowPass(low, 160f); LowPass(low, 160f);
            Normalize(low, 1f);
            float att = 0.045f + 0.005f * v, hold = 0.09f + 0.02f * v, rel = 0.13f + 0.02f * v, wob = 11f + 2f * v;
            for (int i = 0; i < n; i++)
            {
                float t = i / 44100f;
                float env = t < att ? 0.5f - 0.5f * Mathf.Cos(Mathf.PI * t / att) : t < att + hold ? 1f : Mathf.Exp(-(t - att - hold) / rel);
                float mod = 1f + 0.15f * Mathf.Sin(2f * Mathf.PI * wob * t + v);
                band[i] = (band[i] + 0.18f * air[i] + 0.35f * low[i]) * env * mod;
            }
            Normalize(band, 0.3f);
            return band;
        }

        // A skin rub loop (2 s). 0.9.4 (user: it sounded like rubbing carpet): 0.9.0 was 500 Hz - 4 kHz noise with a new random level every
        // 10 ms, a rough crackle like fibres. Skin on skin is a soft, breathy swish: a warm band (180 Hz - 1.2 kHz, two poles each side) with
        // a faint airy sheen (2.5 - 6 kHz at a fifth), its level swelling slowly like a stroke (2.5 / 3.5 / 5 Hz, all periodic in the 2 s loop),
        // no grain. Seamless by a 100 ms equal-power crossfade. Your own recordings in Sounds\rub still come first.
        // 0.11.22 (user: it still sounds like clothing): fabric is the bright rustle above 2.5 kHz; skin sliding on skin is duller and
        // softer: the band now 150 - 850 Hz, the airy sheen at a sixteenth (was a fifth) and below 4 kHz, slower and deeper swells.
        static float[] BuildRub()
        {
            int n = 44100 * 2, x = 4410;
            var s = Noise(n + x, 91);
            HighPass(s, 150f); HighPass(s, 150f); LowPass(s, 850f); LowPass(s, 850f);
            Normalize(s, 1f);
            var air = Noise(n + x, 93);
            HighPass(air, 1800f); HighPass(air, 1800f); LowPass(air, 4000f);
            Normalize(air, 1f);
            var o = new float[n];
            Func<int, float> sample = i =>
            {
                float t = (i % n) / 44100f;
                float mod = 0.7f + 0.2f * Mathf.Sin(2f * Mathf.PI * 1.5f * t + 0.4f) + 0.08f * Mathf.Sin(2f * Mathf.PI * 2.5f * t + 1.9f);
                return (s[i] + 0.06f * air[i]) * mod;
            };
            for (int i = 0; i < n; i++) o[i] = sample(i);
            for (int i = 0; i < x; i++)
            {
                float f = i / (float)x;
                o[i] = o[i] * Mathf.Sin(0.5f * Mathf.PI * f) + sample(n + i) * Mathf.Cos(0.5f * Mathf.PI * f);   // the tail flows into the head
            }
            Normalize(o, 0.3f);
            return o;
        }
    }


    // A voice pack (0.9.0; lines and lazy loading 0.9.1): her reaction voice from WAV files in
    // BepInEx\plugins\SfMorph\Sounds\voice\<pack>\ instead of the game's voice library:
    //   <category>\*.wav  breaths (calm / heavy), moans by intensity (low / mid / high), climax, exertion (short pained / startled sounds);
    //   lines\<bank>[_<level>]\*.wav  spoken lines: nipple / pussy / anal / public by level 1-3 (like the game's DirtyTalk_Library banks),
    //   invite, request (asking for more), afterglow, guard (not yet / give me space), ouch (a slap).
    // Choosing a pack only lists its files; a clip is read when it is first picked (a few ms on the main thread) and the last 64 clips stay
    // in memory, so a pack of thousands of lines costs no more than that.
    internal sealed class VoicePack
    {
        public const int BreathCalm = 0, BreathHeavy = 1, MoanLow = 2, MoanMid = 3, MoanHigh = 4, Climax = 5, Exertion = 6, NC = 7;
        public static readonly string[] Folders = { "breath_calm", "breath_heavy", "moan_low", "moan_mid", "moan_high", "climax", "exertion" };
        public static readonly string[] LineBanks = { "nipple", "pussy", "anal", "public" };   // by level 1-3, same order as ReactionsGame.LineCats
        public static readonly string[] StateLines = { "invite", "request", "afterglow", "guard", "ouch" };
        public const int SInvite = 0, SRequest = 1, SAfterglow = 2, SGuard = 3, SOuch = 4;
        const int Cache = 64;

        // 0.11.35: a shuffled deck per folder (user: the same few lines over and over): every file plays once before any repeats
        sealed class Cat { public string[] Files = new string[0]; public int[] Deck; public int Pos, Last = -1; }
        readonly Cat[] _cats = new Cat[NC];
        readonly Cat[] _lines = new Cat[4 * 3];
        readonly Cat[] _state = new Cat[5];
        readonly Dictionary<string, UnityEngine.Object> _clip = new Dictionary<string, UnityEngine.Object>();
        readonly Dictionary<string, float> _lenOf = new Dictionary<string, float>();
        // 0.9.9: clip -> its file and the seconds the silence trim cut from its start (lip-sync curves follow the file)
        readonly Dictionary<UnityEngine.Object, string> _pathOf = new Dictionary<UnityEngine.Object, string>();
        readonly Dictionary<string, float> _leadOf = new Dictionary<string, float>();
        readonly LinkedList<string> _lru = new LinkedList<string>();
        readonly System.Random _rng = new System.Random();
        MethodInfo _create, _setData;
        public string Name = "", Summary = "";
        public bool Loaded;
        public bool HasLines;
        public UnityEngine.Object Busy1, Busy2;   // clips the game's sources hold now: never evicted

        public static string Root { get { return Path.Combine(SkinFoley.Dir, "voice"); } }

        // The pack folders that exist (sorted), for the panel's chooser.
        public static string[] List()
        {
            try
            {
                if (!Directory.Exists(Root)) return new string[0];
                var d = Directory.GetDirectories(Root);
                var n = new string[d.Length];
                for (int i = 0; i < d.Length; i++) n[i] = Path.GetFileName(d[i]);
                Array.Sort(n, StringComparer.OrdinalIgnoreCase);
                return n;
            }
            catch (Exception) { return new string[0]; }
        }

        static Cat Scan(string d)
        {
            var c = new Cat();
            if (Directory.Exists(d)) { c.Files = Directory.GetFiles(d, "*.wav"); Array.Sort(c.Files, StringComparer.OrdinalIgnoreCase); }
            return c;
        }

        public bool Has(int c) { return Loaded && _cats[c] != null && _cats[c].Files.Length > 0; }
        bool HasLine(int bank, int lvl) { return _lines[bank * 3 + lvl - 1] != null && _lines[bank * 3 + lvl - 1].Files.Length > 0; }
        public bool HasState(int s) { return Loaded && _state[s] != null && _state[s].Files.Length > 0; }

        // Lists pack `name` (empty: unloads). tClip = UnityEngine.AudioClip. Logs one line; never throws.
        public void Load(string name, Type tClip)
        {
            Unload();
            Name = name ?? "";
            if (Name.Length == 0) { Summary = ""; return; }
            try
            {
                _create = tClip != null ? tClip.GetMethod("Create", new[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(bool) }) : null;
                _setData = tClip != null ? tClip.GetMethod("SetData", new[] { typeof(float[]), typeof(int) }) : null;
                string dir = Path.Combine(Root, Name);
                if (_create == null || _setData == null || !Directory.Exists(dir)) { Summary = _create == null ? "AudioClip.Create not found" : "folder not found: " + dir; Plugin.Log.LogWarning("Voice pack '" + Name + "': " + Summary); return; }
                var sb = new System.Text.StringBuilder();
                int total = 0, lines = 0;
                for (int c = 0; c < NC; c++)
                {
                    _cats[c] = Scan(Path.Combine(dir, Folders[c]));
                    total += _cats[c].Files.Length;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(Folders[c]).Append(' ').Append(_cats[c].Files.Length);
                }
                string ld = Path.Combine(dir, "lines");
                var lb = new System.Text.StringBuilder();
                for (int b = 0; b < 4; b++)
                {
                    lb.Append(lb.Length > 0 ? ", " : "").Append(LineBanks[b]);
                    for (int l = 1; l <= 3; l++) { var c = Scan(Path.Combine(ld, LineBanks[b] + "_" + l)); _lines[b * 3 + l - 1] = c; lines += c.Files.Length; lb.Append(l == 1 ? " " : "/").Append(c.Files.Length); }
                }
                for (int s = 0; s < StateLines.Length; s++) { _state[s] = Scan(Path.Combine(ld, StateLines[s])); lines += _state[s].Files.Length; lb.Append(", ").Append(StateLines[s]).Append(' ').Append(_state[s].Files.Length); }
                HasLines = lines > 0;
                Loaded = total + lines > 0;
                Summary = (Loaded ? "" : "no clips: ") + sb + (HasLines ? "; lines: " + lb : "; no lines");
                Plugin.Log.LogInfo("Voice pack '" + Name + "': " + Summary + " (read as they are first used, " + Cache + " kept in memory)");
            }
            catch (Exception e) { Unload(); Summary = "failed: " + e.Message; Plugin.Log.LogWarning("Voice pack '" + name + "' failed: " + e); }
        }

        public void Unload()
        {
            foreach (var kv in _clip) try { if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value); } catch (Exception) { }
            _clip.Clear(); _lenOf.Clear(); _lru.Clear(); _pathOf.Clear(); _leadOf.Clear();
            for (int c = 0; c < NC; c++) _cats[c] = null;
            for (int i = 0; i < _lines.Length; i++) _lines[i] = null;
            for (int i = 0; i < _state.Length; i++) _state[i] = null;
            Loaded = false; HasLines = false;
        }

        // A clip of category c (not one of the last 6 when there are more), or of the nearest category that has files; null = none.
        public UnityEngine.Object Pick(int c, out float len)
        {
            len = 0f;
            if (!Loaded) return null;
            if (!Has(c))
            {
                int[] near = c == BreathCalm ? new[] { BreathHeavy } : c == BreathHeavy ? new[] { BreathCalm } : c == MoanLow ? new[] { MoanMid, MoanHigh }
                           : c == MoanMid ? new[] { MoanLow, MoanHigh } : c == MoanHigh ? new[] { MoanMid, Climax } : c == Climax ? new[] { MoanHigh, MoanMid }
                           : new[] { MoanHigh, MoanMid };
                c = -1;
                foreach (int o in near) if (Has(o)) { c = o; break; }
                if (c < 0) return null;
            }
            return From(_cats[c], out len);
        }

        // A line of bank b (0 nipple, 1 pussy, 2 anal, 3 public) at level 1-3: the nearest level of that bank, else the public bank at that
        // level (an area without lines speaks her general lines). Null = none.
        public UnityEngine.Object PickLine(int b, int lvl, out float len, out int gotBank, out int gotLvl)
        {
            len = 0f; gotBank = -1; gotLvl = 0;
            if (!Loaded || !HasLines) return null;
            lvl = Mathf.Clamp(lvl, 1, 3);
            foreach (int bank in b == 3 ? new[] { 3 } : new[] { b, 3 })
                foreach (int l in new[] { lvl, lvl - 1, lvl + 1, lvl - 2, lvl + 2 })
                {
                    if (l < 1 || l > 3 || !HasLine(bank, l)) continue;
                    var clip = From(_lines[bank * 3 + l - 1], out len);
                    if (clip != null) { gotBank = bank; gotLvl = l; return clip; }
                }
            return null;
        }

        public UnityEngine.Object PickState(int s, out float len)
        {
            len = 0f;
            if (!HasState(s)) return null;
            return From(_state[s], out len);
        }

        UnityEngine.Object From(Cat c, out float len)
        {
            len = 0f;
            int n = c.Files.Length;
            if (n == 0) return null;
            for (int tries = 0; tries < 4; tries++)
            {
                if (c.Deck == null || c.Deck.Length != n || c.Pos >= n)
                {
                    // a new round in a new order; its first clip is not the one that just played
                    if (c.Deck == null || c.Deck.Length != n) { c.Deck = new int[n]; for (int i = 0; i < n; i++) c.Deck[i] = i; }
                    for (int i = n - 1; i > 0; i--) { int j = _rng.Next(i + 1); int t = c.Deck[i]; c.Deck[i] = c.Deck[j]; c.Deck[j] = t; }
                    if (n > 1 && c.Deck[0] == c.Last) { int j = 1 + _rng.Next(n - 1); int t = c.Deck[0]; c.Deck[0] = c.Deck[j]; c.Deck[j] = t; }
                    c.Pos = 0;
                }
                int idx = c.Deck[c.Pos++];
                c.Last = idx;
                var clip = Get(c.Files[idx], out len);
                if (clip != null) return clip;          // an unreadable file: try another
            }
            return null;
        }

        // 0.9.9: the file a clip of this pack was read from and the seconds trimmed from its start. False = not one of the pack's clips.
        public bool Source(UnityEngine.Object clip, out string path, out float lead)
        {
            lead = 0f; path = null;
            if (clip == null || !_pathOf.TryGetValue(clip, out path)) return false;
            _leadOf.TryGetValue(path, out lead);
            return true;
        }

        // The clip of a file, read on first use; the least recently used beyond Cache are destroyed (not one a source holds now).
        UnityEngine.Object Get(string path, out float len)
        {
            UnityEngine.Object clip;
            if (_clip.TryGetValue(path, out clip) && clip != null)
            {
                _lru.Remove(path); _lru.AddFirst(path);
                len = _lenOf[path];
                return clip;
            }
            len = 0f;
            float[] s; int rate, lead; string why;
            if (!SkinFoley.ReadWav(path, 12f, true, out s, out rate, out lead, out why)) { Plugin.Log.LogWarning("Voice pack: skipped " + Path.GetFileName(path) + " (" + why + ")"); return null; }
            clip = _create.Invoke(null, new object[] { "SfVoice_" + Path.GetFileNameWithoutExtension(path), s.Length, 1, rate, false }) as UnityEngine.Object;
            if (clip == null) return null;
            _setData.Invoke(clip, new object[] { s, 0 });
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            len = s.Length / (float)rate;
            _clip[path] = clip; _lenOf[path] = len; _lru.AddFirst(path);
            _pathOf[clip] = path; _leadOf[path] = lead / (float)rate;
            var node = _lru.Last;
            while (_clip.Count > Cache && node != null)
            {
                var prev = node.Previous;
                var o = _clip[node.Value];
                if (!ReferenceEquals(o, Busy1) && !ReferenceEquals(o, Busy2) && !ReferenceEquals(o, clip))
                {
                    try { if (o != null) UnityEngine.Object.Destroy(o); } catch (Exception) { }
                    if (o != null) _pathOf.Remove(o);
                    _clip.Remove(node.Value); _lenOf.Remove(node.Value); _leadOf.Remove(node.Value); _lru.Remove(node);
                }
                node = prev;
            }
            return clip;
        }
    }
}
