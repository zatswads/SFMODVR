#!/usr/bin/env python3
"""Summarise the timing lines of a BepInEx LogOutput.log (SF Body Morphs' Perf / Soft zones clock / Overlap reset lines, and
SF Neural Rendering VR's frame timing lines when present), so an A/B session reads as one table instead of a scroll.

python tools/timing/perf_report.py "E:/Bak/New folder/Misc/SF/VR/BepInEx/LogOutput.log" [--top 8]

Per 10 s window: fps, frame avg/max, fixed steps per frame, our apply step avg, the three biggest of our stages, the frame phases that
dominate (postLate = render submit on the CPU, wait = waiting for the GPU / compositor). Then the soft-zone clock windows and every
warning-level timing signature (time dropped, cuts, resets, long frames), and a verdict line (CPU-bound in ours / in the game / GPU-bound).
"""
import re
import sys

PERF = re.compile(r"Perf last 10 s: ([\d.]+) fps \(frame avg ([\d.]+) max (\d+) ms\), fixed steps/frame avg ([\d.]+) max (\d+)(?:, GPU ([\d.]+) max ([\d.]+) ms)?")
STAGES = re.compile(r"(\w+) ([\d.]+)/([\d.]+)")
CLOCK = re.compile(r"Soft zones clock last 30 s: (\d+) frames, substeps avg ([\d.]+) max (\d+), long frames (\d+), cuts (\d+), clamp ([\d.]+) %, biggest per-frame jump ([\d.]+) mm")
VERSION = re.compile(r"Loading \[(SF Body Morphs|SF Neural Rendering VR) ([\d.]+)\]")
FLAGS = [
    (re.compile(r"Overlap: reset"), "overlap reset"),
    (re.compile(r"Overlap: reset, frame time"), "long frame reset"),
    (re.compile(r"Unity error repeating"), "log spam"),
    (re.compile(r"GC|garbage collect", re.I), "gc"),
]


def parse(path):
    lines = open(path, encoding="utf-8", errors="replace").read().splitlines()
    perf, clock, flags, versions = [], [], [], []
    i = 0
    while i < len(lines):
        ln = lines[i]
        m = VERSION.search(ln)
        if m:
            versions.append("%s %s" % (m.group(1), m.group(2)))
        m = PERF.search(ln)
        if m:
            row = {"line": i + 1, "fps": float(m.group(1)), "avg": float(m.group(2)), "max": int(m.group(3)),
                   "steps": float(m.group(4)), "stepsMax": int(m.group(5)), "gpu": float(m.group(6)) if m.group(6) else None,
                   "ours": {}, "phases": {}}
            for j in (1, 2):
                if i + j < len(lines):
                    nxt = lines[i + j]
                    key = "ours" if "ours ms" in nxt else "phases" if "frame phases" in nxt else None
                    if key:
                        for s in STAGES.finditer(nxt.split(":", 1)[1]):
                            row[key][s.group(1)] = (float(s.group(2)), float(s.group(3)))
            perf.append(row)
        m = CLOCK.search(ln)
        if m:
            clock.append((i + 1,) + tuple(float(x) for x in m.groups()))
        for rx, name in FLAGS:
            if rx.search(ln):
                flags.append((i + 1, name, ln.strip()[:160]))
        i += 1
    return versions, perf, clock, flags


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    versions, perf, clock, flags = parse(sys.argv[1])
    print("versions: " + (", ".join(sorted(set(versions))) or "-"))
    print("\n%6s %6s %7s %5s %5s %7s  %-40s %s" % ("line", "fps", "avg ms", "max", "steps", "ours", "top stages (avg ms)", "phases (avg ms)"))
    for r in perf:
        ours = r["ours"].get("applyAll", (0.0, 0.0))[0]
        top = sorted(((v[0], k) for k, v in r["ours"].items() if k != "applyAll"), reverse=True)[:3]
        ph = sorted(((v[0], k) for k, v in r["phases"].items()), reverse=True)[:3]
        print("%6d %6.1f %7.1f %5d %5.1f %7.2f  %-40s %s" % (r["line"], r["fps"], r["avg"], r["max"], r["steps"], ours,
              ", ".join("%s %.2f" % (k, v) for v, k in top), ", ".join("%s %.1f" % (k, v) for v, k in ph)))
    if clock:
        print("\nsoft zones clock (line: frames, substeps avg/max, long, cuts, clamp %, jump mm)")
        for c in clock:
            warn = []
            if c[4] > 0: warn.append("long frames")
            if c[5] > 0: warn.append("cuts")
            if c[7] > 15: warn.append("jump > 15 mm")
            print("%6d: %d, %.1f/%d, %d, %d, %.1f, %.1f %s" % (c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], ("<- " + ", ".join(warn)) if warn else ""))
    if flags:
        print("\nflags")
        for f in flags[:60]:
            print("%6d %-16s %s" % f)
    steady = [r for r in perf if r["max"] < 500]   # windows without a load hitch
    if steady:
        n = float(len(steady))
        fps = sum(r["fps"] for r in steady) / n
        ours = sum(r["ours"].get("applyAll", (0, 0))[0] for r in steady) / n
        frame = sum(r["avg"] for r in steady) / n
        post = sum(r["phases"].get("postLate", (0, 0))[0] for r in steady) / n
        wait = sum(r["phases"].get("wait", (0, 0))[0] for r in steady) / n
        gpu = [r["gpu"] for r in steady if r["gpu"]]
        verdict = ("GPU-bound (GPU %.1f ms of a %.1f ms frame)" % (sum(gpu) / len(gpu), frame)) if gpu and sum(gpu) / len(gpu) > 0.85 * frame else \
                  ("ours is %.0f %% of the frame" % (100 * ours / frame)) if ours > 0.25 * frame else \
                  ("render submit (postLate %.1f ms) dominates the CPU frame" % post) if post > 0.4 * frame else \
                  ("main thread waits %.1f ms: GPU / compositor bound" % wait) if wait > 0.3 * frame else "mixed"
        print("\nsteady windows: %d, %.1f fps, ours %.2f ms, postLate %.1f ms, wait %.1f ms -> %s" % (len(steady), fps, ours, post, wait, verdict))
    return 0


if __name__ == "__main__":
    sys.exit(main())
