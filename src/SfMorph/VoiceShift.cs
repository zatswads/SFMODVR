using UnityEngine;

namespace SfMorph
{
    // Her voice's pitch and formant (0.9.0; TD-PSOLA since 0.9.3), set separately and without changing the speed (the game's lip sync is
    // baked into its face clips, so the timing must stay). An audio filter (OnAudioFilterRead) on the GameObject of each of the game's voice
    // sources (moan, dirty talk, voice loop, dialogue); it runs on the audio thread and touches nothing in Unity.
    //
    // 0.9.0 shifted the LPC residual with a fixed 25 ms delay-line window: the glottal pulses were smeared by every crossfade and the voice
    // sounded robotic. Now pitch-synchronous overlap-add:
    //   pitch tracking: YIN on a 4x decimated copy (12 kHz at 48 kHz), 80-900 Hz, every 8 ms; voiced below a 0.2 dip;
    //   analysis marks one period apart, each snapped to the strongest sample within +-1/6 period (a glottal pulse);
    //   synthesis: grains around the nearest analysis mark, laid down every period / pitch (voiced) or every 6 ms
    //   (unvoiced: breath, hiss keep their own timing), each grain (one pulse response, windowed over +-1 period of the input) resampled by `formant`.
    // So the vocal tract (formants) stays with pitch alone, and moves alone with formant alone. Latency while on: about 45 ms. Both 1 = bypass.
    internal sealed class VoiceShift : MonoBehaviour
    {
        // written on the main thread, read once per audio block
        public static volatile float Pitch = 1f, Formant = 1f;
        public static int Rate = 48000;

        Psola _ps;
        float[] _g;
        double[] _ec;
        bool _active;

        void OnAudioFilterRead(float[] data, int channels)
        {
            float p = Mathf.Clamp(Pitch, 0.5f, 2f), f = Mathf.Clamp(Formant, 0.5f, 2f);
            if (Mathf.Abs(p - 1f) < 0.004f && Mathf.Abs(f - 1f) < 0.004f) { if (_active) { _active = false; _ps = null; } return; }
            if (_ps == null || _ps.Rate != Rate) _ps = new Psola(Rate);
            if (_g == null || _g.Length != channels) { _g = new float[channels]; _ec = new double[channels]; for (int c = 0; c < channels; c++) _g[c] = 1f; }
            _active = true;
            int n = data.Length / channels;
            // one mono voice in, each channel's share of it out (the source may already be panned)
            double em = 0;
            for (int c = 0; c < channels; c++) _ec[c] = 0;
            for (int i = 0; i < n; i++)
            {
                float m = 0f;
                for (int c = 0; c < channels; c++) { float v = data[i * channels + c]; m += v; _ec[c] += v * v; }
                m /= channels; em += m * m;
            }
            for (int c = 0; c < channels; c++)
            {
                float g = em > 1e-12 ? (float)System.Math.Sqrt(_ec[c] / em) : _g[c];
                _g[c] += (Mathf.Clamp(g, 0f, 4f) - _g[c]) * 0.2f;
            }
            for (int i = 0; i < n; i++)
            {
                float m = 0f;
                for (int c = 0; c < channels; c++) m += data[i * channels + c];
                float y = _ps.Tick(m / channels, p, f);
                if (float.IsNaN(y) || float.IsInfinity(y)) { _ps = new Psola(Rate); y = 0f; }
                float a = y < 0f ? -y : y;
                if (a > 0.8f) y = (y < 0f ? -1f : 1f) * (0.8f + 0.2f * (float)System.Math.Tanh((a - 0.8f) / 0.2f));   // soft limit
                for (int c = 0; c < channels; c++) data[i * channels + c] = y * _g[c];
            }
        }

        // Streaming TD-PSOLA on one mono signal; fixed latency (Latency samples).
        internal sealed class Psola
        {
            public readonly int Rate;
            const int N = 1 << 15, M = N - 1;              // input / output rings
            const int DN = 1 << 12, DM = DN - 1;           // decimated ring
            readonly float[] _in = new float[N], _acc = new float[N], _d = new float[DN];
            readonly float[] _df, _cm;
            readonly int _tMin, _tMax, _hop, _w, _lagMin, _lagMax, _tu, _lat;
            long _n;                                        // samples written
            long _dn; float _dacc; int _dk;                 // decimation
            int _hopCnt;
            float _T; bool _voiced;                         // current pitch period (samples) and voicing
            // analysis marks (a small ring, oldest first)
            readonly double[] _ma = new double[64]; readonly float[] _mt = new float[64]; readonly bool[] _mv = new bool[64];
            int _mHead, _mCount;
            double _nextA, _nextS;

            public Psola(int rate)
            {
                Rate = rate;
                _tMin = Mathf.Max(8, rate / 900); _tMax = rate / 80;
                _tu = Mathf.Max(64, (int)(0.006f * rate));
                _hop = Mathf.Max(128, (int)(0.008f * rate));
                _lagMin = Mathf.Max(2, _tMin / 4); _lagMax = _tMax / 4 + 1;
                _w = Mathf.Max(64, (int)(0.025f * rate) / 4);
                _df = new float[_lagMax + 2]; _cm = new float[_lagMax + 2];
                // a grain's half-length <= tMax; it reads up to H * formant (<= 1.25) + T/2 around its centre
                _lat = 2 * (int)(_tMax * 1.25f + _tMax * 0.5f) + 64;
                _T = rate / 220f; _voiced = false;
            }

            public int Latency { get { return _lat; } }

            public float Tick(float x, float p, float f)
            {
                _in[_n & M] = x;
                // 4x decimation (box average) for the pitch tracker
                _dacc += x; if (++_dk == 4) { _d[_dn & DM] = _dacc * 0.25f; _dn++; _dacc = 0f; _dk = 0; }
                _n++;
                if (++_hopCnt >= _hop) { _hopCnt = 0; Track(); }

                // analysis marks: one period apart (voiced; 6 ms unvoiced), a voiced one snapped to the strongest sample near the prediction
                while (_nextA < _n - _tMax)
                {
                    double a = _nextA;
                    float T = _voiced ? _T : _tu;
                    if (_voiced)
                    {
                        int r = (int)(T / 6f); long best = (long)a; float bv = -1f;
                        for (int k = -r; k <= r; k++)
                        {
                            long j = (long)a + k;
                            if (j < 0 || j >= _n) continue;
                            float v = System.Math.Abs(_in[j & M]);
                            if (v > bv) { bv = v; best = j; }
                        }
                        a = best;
                    }
                    if (_mCount == _ma.Length) { _mHead = (_mHead + 1) % _ma.Length; _mCount--; }
                    int slot = (_mHead + _mCount) % _ma.Length;
                    _ma[slot] = a; _mt[slot] = T; _mv[slot] = _voiced; _mCount++;
                    _nextA = a + T;
                }

                // synthesis grains, laid down up to half the latency ahead of the output
                long outPos = _n - _lat;
                while (_nextS <= _n - _lat / 2 - _tMax / 2 && _mCount > 0)
                {
                    // the analysis mark nearest the synthesis time (input time = output time: no time stretch)
                    int bi = -1; double bd = double.MaxValue;
                    for (int k = 0; k < _mCount; k++)
                    {
                        int s = (_mHead + k) % _ma.Length;
                        double d = System.Math.Abs(_ma[s] - _nextS);
                        if (d < bd) { bd = d; bi = s; } else if (_ma[s] > _nextS) break;
                    }
                    if (bi < 0) break;
                    float T = _mt[bi]; bool v = _mv[bi];
                    float spacing = v ? T / p : T;
                    double c = _ma[bi];
                    long s0 = (long)System.Math.Round(_nextS);
                    // the grain is one pulse response: windowed over +-T in the INPUT (Hann, so grains one period apart sum to exactly 1), then
                    // laid down resampled by f (its output half-length T / f). Voiced grains are added as they are (each output period gets
                    // one pulse response, as in the voice); unvoiced ones are scaled by f so their overlap stays 1 on average.
                    float hOut = T / f;
                    float gain = v ? 1f : f;
                    int h = (int)hOut;
                    for (int k = -h; k <= h; k++)
                    {
                        long o = s0 + k;
                        if (o < outPos || o < 0) continue;
                        float u = k * f;                              // input offset from the pulse
                        if (u <= -T || u >= T) continue;
                        float w = gain * (0.5f + 0.5f * Mathf.Cos(Mathf.PI * u / T));
                        double src = c + u;
                        long i0 = (long)System.Math.Floor(src);
                        if (i0 < 0 || i0 + 1 >= _n || i0 + 1 <= _n - N) continue;
                        float fr = (float)(src - i0);
                        float a0 = _in[i0 & M], a1 = _in[(i0 + 1) & M];
                        _acc[o & M] += w * (a0 + (a1 - a0) * fr);
                    }
                    _nextS += System.Math.Max(spacing, 8f);
                    // forget marks well behind the synthesis time
                    while (_mCount > 1 && _ma[_mHead] < _nextS - 2 * _tMax) { _mHead = (_mHead + 1) % _ma.Length; _mCount--; }
                }

                if (outPos < 0) return 0f;
                long oi = outPos & M;
                float y = _acc[oi];
                _acc[oi] = 0f;
                return y;
            }

            // YIN on the last _w decimated samples: period in full-rate samples and voicing.
            void Track()
            {
                if (_dn < _w + _lagMax + 1) return;
                long end = _dn;
                double e = 0;
                for (int j = 0; j < _w; j++) { float v = _d[(end - _w + j) & DM]; e += v * v; }
                if (e / _w < 1e-7) { _voiced = false; return; }
                for (int tau = 1; tau <= _lagMax; tau++)
                {
                    double s = 0;
                    long b = end - _w - tau;
                    for (int j = 0; j < _w; j++) { float d = _d[(b + j + tau) & DM] - _d[(b + j) & DM]; s += d * d; }
                    _df[tau] = (float)s;
                }
                double run = 0;
                _cm[0] = 1f;
                for (int tau = 1; tau <= _lagMax; tau++) { run += _df[tau]; _cm[tau] = run > 0 ? (float)(_df[tau] * tau / run) : 1f; }
                int t = -1;
                for (int tau = _lagMin; tau < _lagMax; tau++)
                    if (_cm[tau] < 0.2f) { while (tau + 1 < _lagMax && _cm[tau + 1] < _cm[tau]) tau++; t = tau; break; }
                if (t < 1) { _voiced = false; return; }
                // parabolic refinement
                float a = _cm[t - 1], b2 = _cm[t], c = _cm[t + 1], den = a - 2f * b2 + c;
                float tt = t + (Mathf.Abs(den) > 1e-9f ? Mathf.Clamp(0.5f * (a - c) / den, -0.5f, 0.5f) : 0f);
                float T = Mathf.Clamp(tt * 4f, _tMin, _tMax);
                // a jump of more than a third is followed only halfway (octave slips)
                if (_voiced && System.Math.Abs(T - _T) > _T / 3f) T = _T + (T - _T) * 0.5f;
                _T = T; _voiced = true;
            }
        }
    }
}
