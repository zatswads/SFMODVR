using UnityEngine;

namespace SfMorph
{
    // Her voice's pitch and formant (0.9.0), set separately and without changing the speed (the game's lip sync is baked into its face clips,
    // so the timing must stay). An audio filter (OnAudioFilterRead) on the GameObject of each of the game's voice sources (moan, dirty talk,
    // voice loop, dialogue); it runs on the audio thread and touches nothing in Unity.
    //   formant f: a delay-line pitch shifter by f moves the whole voice (pitch and vocal tract) by f;
    //   pitch p:   linear prediction (LPC, order 24, 1024-sample window every 256) splits that into its spectral envelope (the formants, now at
    //              f) and a flat residual (the glottal pulses); the residual alone is shifted by p / f and filtered back through the envelope.
    // So f = p is a plain shift, p alone keeps her vocal tract, f alone keeps her pitch. Both 1 = bypass (no latency, no cost). Latency while
    // on: about 15 ms per active stage.
    internal sealed class VoiceShift : MonoBehaviour
    {
        // written on the main thread, read once per audio block
        public static volatile float Pitch = 1f, Formant = 1f;
        public static int Rate = 48000;

        Chan[] _ch;
        bool _active;

        void OnAudioFilterRead(float[] data, int channels)
        {
            float p = Pitch, f = Formant;
            bool shiftF = Mathf.Abs(f - 1f) > 0.004f, shiftP = Mathf.Abs(p / f - 1f) > 0.004f;
            if (!shiftF && !shiftP) { if (_active) { _active = false; _ch = null; } return; }   // fresh state when turned on again
            if (_ch == null || _ch.Length != channels)
            {
                _ch = new Chan[channels];
                for (int c = 0; c < channels; c++) _ch[c] = new Chan(Rate);
            }
            _active = true;
            float r2 = p / f;
            for (int c = 0; c < channels; c++)
            {
                var ch = _ch[c];
                for (int i = c; i < data.Length; i += channels)
                {
                    float y = data[i];
                    if (shiftF) y = ch.S1.Tick(y, f);
                    if (shiftP)
                    {
                        float x = y;
                        y = ch.Lpc.Synth(ch.S2.Tick(ch.Lpc.Inverse(x), r2));
                        // the first analysis needs a full window, and the residual delay line then still holds 25 ms of unwhitened samples:
                        // the dry signal until both are past, then a 40 ms fade into the processed one
                        if (ch.Lpc.Ready && ch.Wait > 0) ch.Wait--;
                        if (ch.Wait > 0) { y = x; }
                        else
                        {
                            // level match: the re-synthesis keeps the loudness of what went in (~40 ms followers, gain 0.25..2)
                            ch.EIn += (x * x - ch.EIn) * ch.K; ch.EOut += (y * y - ch.EOut) * ch.K;
                            float g = ch.EOut > 1e-9f ? Mathf.Clamp((float)System.Math.Sqrt(ch.EIn / ch.EOut), 0.25f, 2f) : 1f;
                            ch.G += (g - ch.G) * ch.K;
                            y *= ch.G;
                            ch.Mix = Mathf.Min(1f, ch.Mix + ch.K);
                            y = x + (y - x) * ch.Mix;
                        }
                    }
                    if (float.IsNaN(y) || float.IsInfinity(y)) { ch.Reset(); y = 0f; }
                    // soft limit above 0.8 (never a hard edge)
                    float a = y < 0f ? -y : y;
                    if (a > 0.8f) y = (y < 0f ? -1f : 1f) * (0.8f + 0.2f * (float)System.Math.Tanh((a - 0.8f) / 0.2f));
                    data[i] = y;
                }
            }
        }

        sealed class Chan
        {
            public readonly Shifter S1, S2;
            public readonly Lpc Lpc = new Lpc();
            public float EIn, EOut, G = 1f, Mix;
            public int Wait, WaitN;
            public readonly float K;
            public Chan(int rate) { S1 = new Shifter(rate, 0.030f); S2 = new Shifter(rate, 0.025f); K = 1f - Mathf.Exp(-1f / (0.04f * rate)); WaitN = Wait = (int)(0.03f * rate); }
            public void Reset() { S1.Reset(); S2.Reset(); Lpc.Reset(); EIn = EOut = 0f; G = 1f; Mix = 0f; Wait = WaitN; }
        }

        // Two read taps half a window apart sliding through a delay line at (1 - ratio) samples per sample, crossfaded by sin^2 so each tap is
        // silent where it wraps.
        sealed class Shifter
        {
            readonly float[] _buf;
            readonly int _mask;
            readonly float _w;
            int _wp;
            float _d;

            public Shifter(int rate, float sec)
            {
                _w = Mathf.Max(64f, rate * sec);
                int n = 1; while (n < _w * 2 + 8) n <<= 1;
                _buf = new float[n]; _mask = n - 1;
            }

            public void Reset() { System.Array.Clear(_buf, 0, _buf.Length); _d = 0f; }

            public float Tick(float x, float ratio)
            {
                _buf[_wp & _mask] = x;
                _d += 1f - ratio;
                if (_d < 0f) _d += _w; else if (_d >= _w) _d -= _w;
                float d2 = _d + _w * 0.5f; if (d2 >= _w) d2 -= _w;
                float s = Mathf.Sin(Mathf.PI * _d / _w), g1 = s * s;
                float y = g1 * Read(_d) + (1f - g1) * Read(d2);
                _wp++;
                return y;
            }

            float Read(float d)
            {
                float pos = _wp - d - 2f;
                int i = Mathf.FloorToInt(pos);
                float fr = pos - i;
                float a = _buf[i & _mask], b = _buf[(i + 1) & _mask];
                return a + (b - a) * fr;
            }
        }

        // Autocorrelation LPC with a lag window, Levinson-Durbin; the same coefficients for the inverse (FIR) and synthesis (IIR) filter.
        sealed class Lpc
        {
            const int P = 24, N = 1024, Hop = 256, M = 31;   // M: ring mask for the filter memories (32 >= P + 1)
            readonly float[] _a = new float[P + 1], _r = new float[P + 1], _tmp = new float[P + 1], _hist = new float[N], _win = new float[N], _lag = new float[P + 1], _xw = new float[N];
            readonly float[] _xm = new float[M + 1], _ym = new float[M + 1];
            int _hp, _cnt, _xi, _yi, _filled;
            public bool Ready;

            public Lpc()
            {
                for (int i = 0; i < N; i++) _win[i] = 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * i / (N - 1));
                for (int k = 0; k <= P; k++) { float x = 2f * Mathf.PI * 120f * k / 48000f; _lag[k] = Mathf.Exp(-0.5f * x * x); }   // ~120 Hz smoothing of the envelope
                _a[0] = 1f;
            }

            public void Reset()
            {
                System.Array.Clear(_a, 0, _a.Length); _a[0] = 1f;
                System.Array.Clear(_hist, 0, N); System.Array.Clear(_xm, 0, _xm.Length); System.Array.Clear(_ym, 0, _ym.Length);
                _hp = _cnt = _xi = _yi = _filled = 0; Ready = false;
            }

            public float Inverse(float x)
            {
                float e = x;
                for (int k = 1; k <= P; k++) e += _a[k] * _xm[(_xi - k) & M];
                _xm[_xi & M] = x; _xi++;
                _hist[_hp] = x; _hp = (_hp + 1) % N;
                if (_filled < N) _filled++;
                if (++_cnt >= Hop) { _cnt = 0; if (_filled >= N) Analyze(); }   // not before the window is full: a half-empty one gives a wild envelope
                return e;
            }

            public float Synth(float e)
            {
                float y = e;
                for (int k = 1; k <= P; k++) y -= _a[k] * _ym[(_yi - k) & M];
                _ym[_yi & M] = y; _yi++;
                return y;
            }

            void Analyze()
            {
                for (int i = 0; i < N; i++) _xw[i] = _hist[(_hp + i) % N] * _win[i];
                for (int k = 0; k <= P; k++)
                {
                    float s = 0f;
                    for (int i = k; i < N; i++) s += _xw[i] * _xw[i - k];
                    _r[k] = s * _lag[k];
                }
                if (_r[0] < 1e-7f) return;                   // silence: keep the last envelope
                _r[0] *= 1.001f;                             // white-noise floor (-30 dB): keeps the filter well conditioned
                // Levinson-Durbin into _tmp, then copy (a failed recursion keeps the old coefficients)
                System.Array.Clear(_tmp, 0, _tmp.Length); _tmp[0] = 1f;
                float err = _r[0];
                for (int i = 1; i <= P; i++)
                {
                    float acc = _r[i];
                    for (int j = 1; j < i; j++) acc += _tmp[j] * _r[i - j];
                    float k = -acc / err;
                    if (!(Mathf.Abs(k) < 0.9999f)) return;
                    for (int j = 1; j <= i / 2; j++) { float t = _tmp[j]; _tmp[j] += k * _tmp[i - j]; if (j != i - j) _tmp[i - j] += k * t; }
                    _tmp[i] = k;
                    err *= 1f - k * k;
                    if (!(err > 0f)) return;
                }
                System.Array.Copy(_tmp, _a, P + 1);
                if (!Ready) { Ready = true; System.Array.Clear(_ym, 0, _ym.Length); }
            }
        }
    }
}
