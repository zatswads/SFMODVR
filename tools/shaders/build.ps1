# Builds SfMorph's shader bundle (compute shaders for the soft contact deformer) with Unity 2022.3.9f1, the game's version.
# Output: dist\sfmorph_shaders (copy next to SfMorph.dll in BepInEx\plugins).
param([string]$Unity = "C:\Program Files\Unity\Hub\Editor\2022.3.9f1\Editor\Unity.exe")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$proj = Join-Path $PSScriptRoot "SfMorphShaders"
$out = Join-Path $root "dist"
$log = Join-Path $env:TEMP "sfmorph_shaders_build.log"
if (-not (Test-Path $Unity)) { throw "Unity 2022.3.9f1 not found at $Unity (pass -Unity <path to Unity.exe>)" }
& $Unity -batchmode -quit -nographics -projectPath $proj -executeMethod BuildBundles.Build -out $out -logFile $log | Out-Null
$code = $LASTEXITCODE
Select-String -Path $log -Pattern "SfMorph bundle|error|Error" | Select-Object -Last 20 | ForEach-Object { $_.Line }
if ($code -ne 0) { throw "Unity exited with $code (log: $log)" }
