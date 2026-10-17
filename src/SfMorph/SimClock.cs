using System;

namespace SfMorph
{
    // How a per-frame simulation splits its frame into substeps (no UnityEngine here, so tools/timing's offline harness compiles it as is).
    //
    // A fixed-step accumulator without output interpolation (what SoftSim did up to 0.11.47) shows the frame-rate as motion: at 41 fps a
    // 1/120 s step runs 2, 3, 2, 3 ... steps a frame (the jiggle advances 17 then 25 ms on frames 24 ms apart: judder), and at 23 fps
    // MaxSteps 4 plus "behind: drop the rest" threw away a quarter of every frame (slow motion, more or less of it as frame times vary).
    // Equal substeps that add up to exactly the frame time advance the sim by what really passed, every frame; the step length stays at
    // or under the target step until the substep cap, so the springs stay as stable as with the fixed step.
    internal static class SimClock
    {
        // Substeps for a frame of dt seconds: the fewest of length <= step, at least 1, at most max (then each is dt / max). 0 when paused.
        public static int Substeps(float dt, float step, int max)
        {
            if (!(dt > 0f) || !(step > 0f)) return 0;
            int n = (int)Math.Ceiling(dt / step - 1e-3);
            if (n < 1) n = 1;
            if (max >= 1 && n > max) n = max;
            return n;
        }

        // The longest frame a simulation should take as elapsed time (longer = a hitch or a load: clamp, do not spiral or explode).
        public const float MaxFrame = 0.1f;

        // Self-test shared by the offline harness (tools/timing) and the Debug tab's Timing check: a spring (k 260, zeta 0.73, lag 0.25 x the
        // parent's motion, i.e. Softness / Springiness 0.5) riding a 2.1 + 4.2 Hz bounce, stepped with Substeps at 90, 41 and uneven 23 fps
        // (30-60 ms), against a 1/4000 s reference. Passes when no frame rate adds more than `tolerance` RMS error (share of the motion) over
        // the same clock at 120 fps, and every run advanced exactly its real time. A few ms of CPU.
        public static bool SelfTest(out string report, float tolerance = 0.05f)
        {
            const float step = 1f / 120f, k = 260f, c = 2f * 0.725f * 16.1245f, inert = 0.25f;
            const double dur = 6.0, refDt = 1.0 / 4000.0;
            int nRef = (int)(dur / refDt) + 2;
            var reference = new float[nRef];
            {
                float d = 0f, v = 0f; double prev = Bounce(0);
                for (int i = 0; i < nRef; i++)
                {
                    double r = Bounce(i * refDt);
                    d -= (float)(r - prev) * inert; prev = r;
                    v += (-d * k - v * c) * (float)refDt; d += v * (float)refDt;
                    reference[i] = d;
                }
            }
            double baseErr = Run(reference, refDt, dur, step, k, c, inert, 0, out bool baseTime);
            var sb = new System.Text.StringBuilder("120 fps " + baseErr.ToString("0.000"));
            bool ok = baseTime;
            for (int mode = 1; mode <= 3; mode++)
            {
                bool timeOk;
                double e = Run(reference, refDt, dur, step, k, c, inert, mode, out timeOk);
                bool pass = timeOk && e <= baseErr + tolerance;
                ok &= pass;
                sb.Append(mode == 1 ? ", 90 fps " : mode == 2 ? ", 41 fps " : ", 23 fps uneven ").Append(e.ToString("0.000")).Append(pass ? "" : timeOk ? " (too far off)" : " (lost time)");
            }
            report = sb.ToString();
            return ok;
        }

        static double Bounce(double t) { return 0.03 * Math.Sin(2 * Math.PI * 2.1 * t) + 0.01 * Math.Sin(2 * Math.PI * 4.2 * t + 0.7); }

        // mode 0: 120 fps, 1: 90 fps, 2: 41 fps, 3: uneven 30-60 ms (fixed seed)
        static double Run(float[] reference, double refDt, double dur, float step, float k, float c, float inert, int mode, out bool timeOk)
        {
            float d = 0f, v = 0f; double t = 0, prev = Bounce(0), simT = 0, se = 0, sr = 0;
            uint seed = 12345u;
            while (t < dur)
            {
                float dt = mode == 0 ? 1f / 120f : mode == 1 ? 1f / 90f : mode == 2 ? 1f / 41f : 0f;
                if (mode == 3) { seed = seed * 1664525u + 1013904223u; dt = 0.030f + 0.030f * ((seed >> 8) / 16777216f); }
                t += dt;
                double r = Bounce(t);
                float lag = (float)(r - prev) * inert; prev = r;
                float dc = Math.Min(dt, MaxFrame);
                int n = Substeps(dc, step, 8);
                float h = dc / n;
                for (int s = 0; s < n; s++) { d -= lag / n; v += (-d * k - v * c) * h; d += v * h; }
                simT += h * n;
                if (t < 1.0) continue;
                int ri = (int)Math.Round(t / refDt);
                if (ri >= reference.Length) break;
                double e = d - reference[ri];
                se += e * e; sr += (double)reference[ri] * reference[ri];
            }
            timeOk = Math.Abs(simT - t) < 1e-3 * t + 1e-4;
            return sr > 0 ? Math.Sqrt(se / sr) : double.NaN;
        }

        // A teleport / cut test that holds at any frame rate: moved further than `metres`, or than `speed` m/s for this frame's dt.
        public static bool Jumped(float dist2, float metres, float speed, float dt)
        {
            float lim = Math.Max(metres, speed * dt);
            return dist2 > lim * lim;
        }
    }
}
