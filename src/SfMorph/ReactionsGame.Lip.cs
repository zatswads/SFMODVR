using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace SfMorph
{
    // 0.9.9: what the lip sync needs from her voice sources, and the dump of the game's voice clips to WAV (for baking curves).
    internal sealed partial class ReactionsGame
    {
        // The voice clip sounding now and its playback time in clip seconds: a voice pack clip on the moan source (Play), a pack breath
        // (PlayOneShot: our own clock, AudioSource.time does not follow one-shots), else the game's clip on the dirty-talk or moan source.
        // pack: the clip is one of the voice pack's. False = nothing of hers sounds.
        public bool VoiceNow(out UnityEngine.Object clip, out float time, out float pitch, out bool pack)
        {
            clip = null; time = 0f; pitch = 1f; pack = false;
            if (!Found || _pClip == null || _pTime == null) return false;
            try
            {
                if (_ownOneShot && _now < _packBreathEnd && Pack.Busy2 != null && _srcMoan != null && !Dead(_srcMoan))
                {
                    clip = Pack.Busy2; time = Mathf.Max(0f, _now - _ownT0); pack = true;
                    return true;
                }
                if (_packClip && !_ownOneShot && Playing(_srcMoan))
                {
                    clip = _pClip.GetValue(_srcMoan, null) as UnityEngine.Object;
                    if (clip == null) return false;
                    time = Convert.ToSingle(_pTime.GetValue(_srcMoan, null)); pitch = GetPitch(_srcMoan, 1f); pack = true;
                    return true;
                }
                if (DirtyTalkPlaying && Playing(_srcDirty))
                {
                    clip = _pClip.GetValue(_srcDirty, null) as UnityEngine.Object;
                    if (clip != null) { time = Convert.ToSingle(_pTime.GetValue(_srcDirty, null)); pitch = GetPitch(_srcDirty, 1f); return true; }
                }
                if (!_packClip && !_ownOneShot && Playing(_srcMoan))
                {
                    clip = _pClip.GetValue(_srcMoan, null) as UnityEngine.Object;
                    if (clip != null) { time = Convert.ToSingle(_pTime.GetValue(_srcMoan, null)); pitch = GetPitch(_srcMoan, 1f); return true; }
                }
            }
            catch (Exception e) { LogOnce("voicenow:" + e.GetType().Name, "reading the voice source for the lip sync failed: " + e.Message); }
            clip = null;
            return false;
        }

        // AudioClip.samples (the game clip's curve key), 0 when unreadable
        PropertyInfo _pSamples, _pChannels, _pFreq;
        MethodInfo _miGetData, _miLoadData;
        bool _clipMembersTried;
        void ClipMembers()
        {
            if (_clipMembersTried || _pClip == null) return;
            _clipMembersTried = true;
            var tc = _pClip.PropertyType;
            _pSamples = tc.GetProperty("samples"); _pChannels = tc.GetProperty("channels"); _pFreq = tc.GetProperty("frequency");
            _miGetData = tc.GetMethod("GetData", new[] { typeof(float[]), typeof(int) });
            _miLoadData = tc.GetMethod("LoadAudioData", Type.EmptyTypes);
        }

        public int ClipSamples(UnityEngine.Object clip)
        {
            ClipMembers();
            if (clip == null || _pSamples == null) return 0;
            try { return Convert.ToInt32(_pSamples.GetValue(clip, null)); } catch (Exception) { return 0; }
        }

        // ---- the dump: every AudioClip (and AudioClip array) field of her voice library and dirty-talk library for the current voice
        // language, to BepInEx\config\SfMorphs\voice-dump\lang<k>\<library>\<field>\<clip>_<samples>.wav, 3 clips per frame ----
        public string DumpStatus = "";
        public bool Dumping { get { return _dump != null; } }
        sealed class DumpItem { public UnityEngine.Object Clip; public string Path; }
        Queue<DumpItem> _dump;
        int _dumpOk, _dumpSkip, _dumpFail, _dumpSeen;
        string _dumpDir;

        public void StartDump()
        {
            if (_dump != null) return;
            ClipMembers();
            if (!Found || _lv == null) { DumpStatus = "off: her game link is not found yet"; return; }
            if (_miGetData == null || _pSamples == null || _pChannels == null || _pFreq == null) { DumpStatus = "off: AudioClip.GetData / samples / channels / frequency not found"; return; }
            int lang = Int(_mLang, _gs, 0);
            _dumpDir = Path.Combine(LipSync.DumpDir, "lang" + lang);
            var q = new Queue<DumpItem>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var libs = new List<KeyValuePair<string, object>>();
            try
            {
                var a = _mLib.Ok ? _mLib.Get(_lv) as Array : null;
                if (a != null && lang >= 0 && lang < a.Length && a.GetValue(lang) != null) libs.Add(new KeyValuePair<string, object>("voice", a.GetValue(lang)));
                var d = _mDtLib != null && _mDtLib.Ok ? _mDtLib.Get(_lv) as Array : null;
                if (d != null && lang >= 0 && lang < d.Length && d.GetValue(lang) != null) libs.Add(new KeyValuePair<string, object>("dirtytalk", d.GetValue(lang)));
            }
            catch (Exception e) { DumpStatus = "off: the voice libraries are unreadable (" + e.Message + ")"; return; }
            if (libs.Count == 0) { DumpStatus = "off: no voice library for language " + lang; return; }
            var tClip = _pClip.PropertyType;
            foreach (var kv in libs)
                for (var t = kv.Value.GetType(); t != null && t != typeof(object) && t != typeof(MonoBehaviour); t = t.BaseType)
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        object v;
                        try { v = f.GetValue(kv.Value); } catch (Exception) { continue; }
                        var list = new List<UnityEngine.Object>();
                        if (tClip.IsAssignableFrom(f.FieldType)) { var o = v as UnityEngine.Object; if (o != null) list.Add(o); }
                        else if (f.FieldType.IsArray && tClip.IsAssignableFrom(f.FieldType.GetElementType()) && v is Array)
                            foreach (var o in (Array)v) { var u = o as UnityEngine.Object; if (u != null) list.Add(u); }
                        else if (typeof(System.Collections.IList).IsAssignableFrom(f.FieldType) && f.FieldType.IsGenericType && tClip.IsAssignableFrom(f.FieldType.GetGenericArguments()[0]) && v != null)
                            foreach (var o in (System.Collections.IList)v) { var u = o as UnityEngine.Object; if (u != null) list.Add(u); }
                        foreach (var c in list)
                        {
                            string nm = LipSync.DumpName(c.name, ClipSamples(c));
                            if (!names.Add(nm)) continue;
                            q.Enqueue(new DumpItem { Clip = c, Path = Path.Combine(Path.Combine(Path.Combine(_dumpDir, kv.Key), LipSync.SafeName(f.Name)), nm + ".wav") });
                        }
                    }
            _dumpOk = _dumpSkip = _dumpFail = 0; _dumpSeen = q.Count;
            if (q.Count == 0) { DumpStatus = "no voice clips found in the libraries"; return; }
            _dump = q;
            DumpStatus = "dumping " + q.Count + " clips to " + _dumpDir;
            Log("voice dump: " + q.Count + " clips (language " + lang + ") to " + _dumpDir);
        }

        // Pump: a few clips per frame (GetData is main-thread only)
        void DumpTick()
        {
            if (_dump == null) return;
            for (int k = 0; k < 3 && _dump.Count > 0; k++)
            {
                var it = _dump.Dequeue();
                if (File.Exists(it.Path)) { _dumpSkip++; continue; }
                string why;
                if (WriteClip(it.Clip, it.Path, out why)) _dumpOk++;
                else { _dumpFail++; if (_dumpFail <= 20) Log("voice dump: " + (it.Clip != null ? it.Clip.name : "?") + " not written (" + why + ")"); }
            }
            DumpStatus = "dumping: " + (_dumpOk + _dumpSkip + _dumpFail) + " of " + _dumpSeen + " (" + _dumpOk + " written, " + _dumpSkip + " already there, " + _dumpFail + " failed)";
            if (_dump.Count == 0)
            {
                _dump = null;
                DumpStatus = "dumped " + _dumpOk + " new clips (" + _dumpSkip + " already there, " + _dumpFail + " failed) to " + _dumpDir;
                Log("voice dump: " + DumpStatus + (_dumpFail > 0 ? "; failed clips are streamed or compressed in memory: extract those from the game's assets" : ""));
            }
        }

        bool WriteClip(UnityEngine.Object clip, string path, out string why)
        {
            why = null;
            if (clip == null) { why = "destroyed"; return false; }
            try
            {
                int n = Convert.ToInt32(_pSamples.GetValue(clip, null)), ch = Convert.ToInt32(_pChannels.GetValue(clip, null)), rate = Convert.ToInt32(_pFreq.GetValue(clip, null));
                if (n <= 0 || ch <= 0 || rate <= 0) { why = "no samples"; return false; }
                if ((long)n * ch > 64L * 1024 * 1024) { why = "too long"; return false; }
                if (_miLoadData != null) try { _miLoadData.Invoke(clip, null); } catch (Exception) { }
                var data = new float[n * ch];
                var ok = _miGetData.Invoke(clip, new object[] { data, 0 });   // timing-lint: ok once per clip
                if (ok is bool && !(bool)ok) { why = "GetData refused (streamed or compressed in memory)"; return false; }
                float pk = 0f; for (int i = 0; i < data.Length; i++) pk = Mathf.Max(pk, Mathf.Abs(data[i]));
                if (pk < 1e-6f) { why = "GetData returned silence (streamed or compressed in memory?)"; return false; }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var fs = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write))
                using (var w = new BinaryWriter(fs))
                {
                    int bytes = data.Length * 2;
                    w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }); w.Write(36 + bytes);
                    w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                    w.Write(16); w.Write((short)1); w.Write((short)ch); w.Write(rate); w.Write(rate * ch * 2); w.Write((short)(ch * 2)); w.Write((short)16);
                    w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); w.Write(bytes);
                    for (int i = 0; i < data.Length; i++) w.Write((short)Mathf.Clamp(Mathf.RoundToInt(data[i] * 32767f), -32768, 32767));
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
                return true;
            }
            catch (Exception e) { why = e.GetType().Name + ": " + e.Message; return false; }
        }
    }
}
