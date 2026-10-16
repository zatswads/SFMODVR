@echo off
rem bake.bat <wav folder> <csv folder> [a2f-bake options]   (default model: v3.0 diffusion, Claire)
if "%A2F_ROOT%"=="" set A2F_ROOT=D:\A2F
set PATH=%A2F_ROOT%\sdk\_build\release\audio2x-sdk\bin;%A2F_ROOT%\cuda-12.9\bin;%A2F_ROOT%\TensorRT-10.13.3.9\lib;%PATH%
"%A2F_ROOT%\bake\a2f-bake.exe" --model "%A2F_ROOT%\sdk\_data\generated\audio2face-sdk\samples\data\multi-diffusion\model.json" --in %1 --out %2 %3 %4 %5 %6 %7 %8
