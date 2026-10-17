using System;
using System.Collections.Generic;
using SfMorph;

// Offline check that the soft-zone jiggle (SoftSim's helper springs) moves the same at any frame rate, with even or uneven frames.
// A 1-D model of one helper with the plugin's spring step (semi-implicit Euler, unit mass, k and damping from Softness / Springiness) and
// its lag behind the bone (inert * the bone's motion), driven by a dance-like bounce of the bone. Each frame sequence is simulated with
// the old clock (fixed 1/120 s accumulator, 4 steps max, drop the rest) and the new one (SimClock.Substeps: equal substeps adding up to
// the frame, the lag spread over them), and compared with a 1/4000 s reference at the frame times.
// Run: dotnet run -c Release --project tools/timing/Harness.csproj   (exit code 1 = the new clock missed the tolerance)
internal static class Program
{
    const float Step = 1f / 120f;
    const int OldMax = 4, NewMax = 8;
    const double Duration = 8.0, Warmup = 1.0;
    // RMS error of the jiggle vs the reference, as a share of the reference's RMS. The spring step itself (1/120 s) is 4-9 % off the
    // continuous reference at any frame rate; what must not happen is the frame rate adding to that: at most Tolerance more than the
    // new clock at 120 fps (one step a frame), and never worse than the old clock.
    const double Tolerance = 0.05;

    struct Tissue { public string Name; public float K, C, Inert; }

    static Tissue Make(string name, float softness, float spring, float kMul)
    {
        float k = (400f + (120f - 400f) * softness) * kMul;
        float zeta = 1f + (0.45f - 1f) * spring;
        float inert = 0.5f * spring;
        return new Tissue { Name = name, K = k, C = 2f * zeta * (float)Math.Sqrt(k), Inert = inert };
    }

    // her bone: a dance bounce (2.1 Hz, 3 cm) with a faster component (4.2 Hz, 1 cm)
    static double Bone(double t) { return 0.03 * Math.Sin(2 * Math.PI * 2.1 * t) + 0.01 * Math.Sin(2 * Math.PI * 4.2 * t + 0.7); }

    static int Main()
    {
        var tissues = new[] { Make("default", 0.5f, 0.5f, 1f), Make("breast-soft", 0.8f, 0.9f, 0.5f), Make("firm", 0.2f, 0.2f, 1.5f) };
        var seqs = new List<KeyValuePair<string, float[]>>
        {
            Seq("90 fps", n => 1f / 90f),
            Seq("72 fps", n => 1f / 72f),
            Seq("41 fps", n => 1f / 41f),
            Seq("23 fps", n => 1f / 23f),
            Seq("23 fps uneven 30-60 ms", Uneven(0.030f, 0.060f, 1)),
            Seq("41 fps uneven 18-32 ms", Uneven(0.018f, 0.032f, 2)),
        };
        bool ok = true;
        Console.WriteLine("tissue        frames                     old err  old time  new err  new time");
        foreach (var ts in tissues)
        {
            var reference = Reference(ts);
            double bt; double baseErr = Error(ts, Seq("120", n => 1f / 120f).Value, reference, true, out bt);
            Console.WriteLine(string.Format("{0,-13} {1,-26} {2,7}  {3,8}  {4,7:0.000}  (baseline)", ts.Name, "120 fps", "", "", baseErr));
            foreach (var kv in seqs)
            {
                double oldT, newT;
                double eOld = Error(ts, kv.Value, reference, false, out oldT);
                double eNew = Error(ts, kv.Value, reference, true, out newT);
                bool pass = !double.IsNaN(eNew) && eNew <= baseErr + Tolerance && eNew <= eOld && Math.Abs(newT - 1.0) < 0.01;
                if (!pass) ok = false;
                Console.WriteLine(string.Format("{0,-13} {1,-26} {2,7:0.000}  {3,7:0.00}x  {4,7:0.000}  {5,7:0.00}x {6}",
                    ts.Name, kv.Key, eOld, oldT, eNew, newT, pass ? "" : "FAIL"));
            }
        }
        // a hitch (one 300 ms frame, then 23 fps): the new clock must stay finite and settle
        {
            var dts = Seq("hitch", n => n == 100 ? 0.3f : 1f / 23f).Value;
            double t; double e = Error(tissues[1], dts, Reference(tissues[1]), true, out t);
            bool pass = !double.IsNaN(e) && e < 0.5;
            if (!pass) ok = false;
            Console.WriteLine("hitch 300 ms then 23 fps: new err " + e.ToString("0.000") + (pass ? "" : " FAIL"));
        }
        // the cut test holds at any frame rate: 20 cm or 8 m/s
        if (SimClock.Jumped(0.15f * 0.15f, 0.2f, 8f, 1f / 23f) || !SimClock.Jumped(0.5f * 0.5f, 0.2f, 8f, 1f / 23f)
            || SimClock.Jumped(0.3f * 0.3f, 0.2f, 8f, 1f / 23f) || !SimClock.Jumped(0.25f * 0.25f, 0.2f, 8f, 1f / 90f))
        { ok = false; Console.WriteLine("SimClock.Jumped: FAIL"); }
        if (SimClock.Substeps(1f / 23f, Step, NewMax) != 6 || SimClock.Substeps(1f / 120f, Step, NewMax) != 1 || SimClock.Substeps(0.1f, Step, NewMax) != 8
            || SimClock.Substeps(0f, Step, NewMax) != 0)
        { ok = false; Console.WriteLine("SimClock.Substeps: FAIL"); }
        // the in-game smoke test (Debug tab, Timing check) runs SimClock.SelfTest: it must pass here too
        string selfRep;
        bool self = SimClock.SelfTest(out selfRep);
        Console.WriteLine("SimClock.SelfTest: " + (self ? "PASS" : "FAIL") + " (" + selfRep + ")");
        if (!self) ok = false;
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    static KeyValuePair<string, float[]> Seq(string name, Func<int, float> dt)
    {
        var l = new List<float>();
        double t = 0; int n = 0;
        while (t < Duration) { float d = dt(n++); l.Add(d); t += d; }
        return new KeyValuePair<string, float[]>(name, l.ToArray());
    }

    static Func<int, float> Uneven(float lo, float hi, uint seed)
    {
        uint s = seed * 2654435761u + 1u;
        return n => { s = s * 1664525u + 1013904223u; return lo + (hi - lo) * ((s >> 8) / 16777216f); };
    }

    // the reference: 1/4000 s steps, the lag continuous; the jiggle (Disp) sampled every 1/4000 s
    const double RefDt = 1.0 / 4000.0;
    static float[] Reference(Tissue ts)
    {
        int n = (int)(Duration / RefDt) + 2;
        var o = new float[n];
        float disp = 0f, vel = 0f; double prev = Bone(0);
        for (int i = 0; i < n; i++)
        {
            double t = i * RefDt;
            double r = Bone(t);
            float h = (float)RefDt;
            disp -= (float)(r - prev) * ts.Inert; prev = r;
            vel += (-disp * ts.K - vel * ts.C) * h;
            disp += vel * h;
            o[i] = disp;
        }
        return o;
    }

    static double Error(Tissue ts, float[] dts, float[] reference, bool newClock, out double timeRatio)
    {
        float disp = 0f, vel = 0f, acc = 0f;
        double t = 0, prev = Bone(0), simT = 0, realT = 0;
        double se = 0, sr = 0;
        foreach (var raw in dts)
        {
            t += raw;
            float dt = Math.Min(raw, SimClock.MaxFrame);
            double r = Bone(t);
            float lag = (float)(r - prev) * ts.Inert; prev = r;
            if (newClock)
            {
                int n = SimClock.Substeps(dt, Step, NewMax);
                float h = dt / n;
                for (int k = 0; k < n; k++)
                {
                    disp -= lag / n;
                    vel += (-disp * ts.K - vel * ts.C) * h;
                    disp += vel * h;
                }
                simT += dt;
            }
            else
            {
                disp -= lag;   // once per frame, before the steps (SoftSim up to 0.11.47)
                acc += dt;
                int steps = 0;
                while (acc >= Step && steps < OldMax)
                {
                    vel += (-disp * ts.K - vel * ts.C) * Step;
                    disp += vel * Step;
                    acc -= Step; steps++;
                    simT += Step;
                }
                if (acc > Step) acc = 0f;
            }
            realT += raw;
            if (t < Warmup) continue;
            int ri = (int)Math.Round(t / RefDt);
            if (ri >= reference.Length) break;
            double d = disp - reference[ri];
            se += d * d; sr += (double)reference[ri] * reference[ri];
        }
        timeRatio = realT > 0 ? simT / realT : 0;
        return sr > 0 ? Math.Sqrt(se / sr) : double.NaN;
    }
}
