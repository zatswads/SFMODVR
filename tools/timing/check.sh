#!/bin/sh
# Pre-release timing gate (no Unity, no game): the hot-path / frame-rate lint over src/SfMorph and the offline clock harness.
#   sh tools/timing/check.sh            -> exit 1 if either fails
# A lint finding that is fine (load-time, every few seconds, audio-rate) gets `timing-lint: ok <why>` on its line instead of being ignored.
set -e
cd "$(dirname "$0")/../.."
DOTNET="${DOTNET:-dotnet}"
command -v "$DOTNET" >/dev/null 2>&1 || DOTNET="$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe"
python tools/timing/lint_timing.py src/SfMorph
"$DOTNET" run -c Release --project tools/timing/Harness.csproj
