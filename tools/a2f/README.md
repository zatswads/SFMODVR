# a2f-bake: offline lip-sync curves (NVIDIA Audio2Face-3D)

Bakes every WAV under a folder into one CSV per clip: `t` (seconds), then the 52 ARKit skin weights and 16 tongue weights
of Audio2Face-3D's blendshape solve, one row per frame. The CSV mirrors the WAV's relative path. The plugin will play
these curves in step with the clip and map the mouth weights onto her face bones (design and plan:
`research-audio2face.md` in the session notes).

Nothing from NVIDIA lives in this repo: no SDK, models, engines or baked curves. The baked curves come from game and
voice-pack audio, so keep them with the user's files.

## Setup (once, about 10 GB; everything under `D:\A2F`, nothing installed system-wide)

| Folder | What | Source |
|---|---|---|
| `cuda-12.9\` | CUDA 12.9.1 redist zips, merged: nvcc, cudart, cublas, curand, nvrtc, cccl, nvjitlink, nvtx, profiler_api, VS integration | developer.download.nvidia.com/compute/cuda/redist (`redistrib_12.9.1.json`, sha256-checked) |
| `TensorRT-10.13.3.9\` | TensorRT for CUDA 12.9 (Windows zip) | developer.download.nvidia.com/compute/machine-learning/tensorrt/10.13.3/zip/ (public, no login) |
| `sdk\` | github.com/NVIDIA/Audio2Face-3D-SDK (MIT) | `git clone`, `git lfs pull --include="sample-data/*"` |
| `sdk\_data\audio2face-models\` | `audio2face-3d-v2.3.1-claire`, `audio2face-3d-v3.0` (NVIDIA Open Model License, not gated) | huggingface.co/nvidia |

1. Fetch the deps: `set PM_PACKAGES_ROOT=D:\A2F\packman-cache` then `fetch_deps.bat release` in `sdk\`.
2. Build the SDK. build.bat's vswhere lookup fails here, so run the same steps with the VS 2022 path set:
   vcvarsall x64, then cmake `-G Ninja -DCMAKE_BUILD_TYPE=release -DCMAKE_CUDA_ARCHITECTURES=89` with
   `CUDA_PATH` / `CUDAToolkit_ROOT` = `D:\A2F\cuda-12.9` and `TENSORRT_ROOT_DIR` = `D:\A2F\TensorRT-10.13.3.9`.
3. Build the TensorRT engines. Run the SDK's `audio2face-sdk/scripts/gen_sample_data.py` steps
   (`regression_gen_data("audio2face-3d-v2.3.1-claire", "claire")`, `diffusion_gen_data("audio2face-3d-v3.0", "multi-diffusion")`)
   with TensorRT's `bin`/`lib` on PATH. Only numpy is needed, so any Python 3 works. Output goes to
   `sdk\_data\generated\audio2face-sdk\samples\data\{claire,multi-diffusion}\network.trt`. Engines are per GPU: rebuild
   them on a new card.
4. `tools\a2f\build.bat` builds `D:\A2F\bake\a2f-bake.exe`. Set `A2F_ROOT` for another location.

## Use

```
tools\a2f\bake.bat <wav folder> <csv folder> [--emotion outofbreath=0.4,pain=0.2] [--force]
```

- Default model: v3.0 diffusion, identity Claire, 60 fps, constant noise (reproducible). For the v2.3.1 regression model use
  `--model ...\claire\model.json --type regression --fps 30`.
- Any WAV works: PCM 8/16/24/32 or float, extensible, any channel count and rate. Input is downmixed to mono and resampled
  to 16 kHz with a windowed sinc.
- Emotions (v3.0): amazement, anger, cheekiness, disgust, fear, grief, joy, outofbreath, pain, sadness. The default is
  neutral (all 0). The emotion is fixed for the whole run, so bake one category folder per emotion setting if needed.
- Incremental: a CSV newer than its WAV is skipped. `curves.txt` in the output folder gets one line per baked clip
  (path, seconds, frames, peak jawOpen).
- Speed on an RTX 4090 laptop: about 10x real time (124 s of audio in 12.6 s, 8 tracks).
