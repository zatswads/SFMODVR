#!/usr/bin/env python3
"""Frame-timing / hot-path lint for the plugin's C# sources (no Unity needed).

python tools/timing/lint_timing.py [src/SfMorph]      -> exit 1 on a finding

Flags patterns that made motion depend on the frame rate or put garbage on every frame:
  acc-drop      a fixed-step accumulator that throws the remainder away (`if (_acc > Step) _acc = 0`): time lost every slow frame
  fixed-acc     `while (acc >= Step ...)` stepping without the remainder being interpolated: steps alternate with the frame rate (judder)
  const-smooth  `x = Lerp(x, target, 0.5f)` / `x += (t - x) * 0.2f` once per frame: the smoothing time follows the frame rate
  global-time   writes to Time.fixedDeltaTime / maximumDeltaTime / captureFramerate / Application.targetFrameRate / vSyncCount
  all-cameras   Camera.allCameras (a new array per read) - use a cached list / Camera.GetAllCameras(buffer)
  invoke-alloc  MethodInfo.Invoke(..., new object[] {...}) - bind a delegate or reuse one argument array

A line that is fine as it is carries `timing-lint: ok <why>` in a comment (on that line or the line above).
"""
import os
import re
import sys

RULES = [
    ("acc-drop", re.compile(r"if\s*\(\s*_?\w*[aA]cc\w*\s*>\s*\w+\s*\)\s*_?\w*[aA]cc\w*\s*=\s*0")),
    ("fixed-acc", re.compile(r"while\s*\(\s*_?\w*[aA]cc\w*\s*>=\s*\w+")),
    ("const-smooth", re.compile(r"(\b[\w.\[\]]+)\s*=\s*(?:Vector3|Quaternion|Mathf|Color)\.S?[lL]erp\(\s*\1\s*,[^;]*,\s*0?\.\d+f\s*\)")),
    ("const-smooth", re.compile(r"(\b[\w.\[\]]+)\s*\+=\s*\([^;]*-\s*\1\s*\)\s*\*\s*0?\.\d+f\s*;")),
    ("global-time", re.compile(r"\b(Time\.(fixedDeltaTime|maximumDeltaTime|captureFramerate|timeScale)|Application\.targetFrameRate|QualitySettings\.vSyncCount)\s*=[^=]")),
    ("all-cameras", re.compile(r"Camera\.allCameras\b")),
    ("invoke-alloc", re.compile(r"\.Invoke\([^;]*new\s+object\s*\[")),
]
OK = re.compile(r"timing-lint:\s*ok")


def lint(root):
    findings = []
    for dirpath, _, files in os.walk(root):
        for f in sorted(files):
            if not f.endswith(".cs"):
                continue
            path = os.path.join(dirpath, f)
            with open(path, encoding="utf-8", errors="replace") as fh:
                lines = fh.read().splitlines()
            for i, line in enumerate(lines):
                code = line.split("//", 1)[0]
                for name, rx in RULES:
                    if rx.search(code) and not OK.search(line) and not (i > 0 and OK.search(lines[i - 1])):
                        findings.append("%s:%d: %s: %s" % (path, i + 1, name, line.strip()[:160]))
    return findings


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, "..", "..", "src", "SfMorph")
    found = lint(root)
    for f in found:
        print(f)
    print("timing lint: %d finding(s)" % len(found))
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main())
