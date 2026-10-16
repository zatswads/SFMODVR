@echo off
rem Builds a2f-bake.exe against the Audio2Face SDK package. Set A2F_ROOT if it isn't D:\A2F (layout: README.md).
if "%A2F_ROOT%"=="" set A2F_ROOT=D:\A2F
set CUDA_PATH=%A2F_ROOT%\cuda-12.9
set HERE=%~dp0
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul || exit /b 1
set PATH=%A2F_ROOT%\sdk\_deps\build-deps\ninja;%PATH%
set CMAKE=%A2F_ROOT%\sdk\_deps\build-deps\cmake\bin\cmake.exe
%CMAKE% -S "%HERE%." -B "%A2F_ROOT%\bake\build" -G Ninja -DCMAKE_BUILD_TYPE=Release -DA2F_SDK=%A2F_ROOT%/sdk/_build/release/audio2x-sdk || exit /b 1
%CMAKE% --build "%A2F_ROOT%\bake\build" || exit /b 1
copy /y "%A2F_ROOT%\bake\build\a2f-bake.exe" "%A2F_ROOT%\bake\" >nul
echo built %A2F_ROOT%\bake\a2f-bake.exe
