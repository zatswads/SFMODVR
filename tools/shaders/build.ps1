# Builds SfMorph's shader bundle (compute shaders for the soft contact deformer, wrinkles) with Unity 2022.3.9f1, the game's version.
# Output: dist\sfmorph_shaders (copy next to SfMorph.dll in BepInEx\plugins).
# Packages/manifest.json must enable com.unity.modules.assetbundle: without it the editor writes the bundle with no AssetBundle object
# and the game refuses it ("not compatible with this newer version of the Unity runtime", 0.7.3-0.7.6).
param([string]$Unity = "C:\Program Files\Unity\Hub\Editor\2022.3.9f1\Editor\Unity.exe")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$proj = Join-Path $PSScriptRoot "SfMorphShaders"
$dist = Join-Path $root "dist"
$out = Join-Path $proj "Build"   # BuildAssetBundles also writes a folder-named manifest bundle: keep it out of dist
$log = Join-Path $env:TEMP "sfmorph_shaders_build.log"
if (-not (Test-Path $Unity)) { throw "Unity 2022.3.9f1 not found at $Unity (pass -Unity <path to Unity.exe>)" }
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
& $Unity -batchmode -quit -nographics -projectPath $proj -executeMethod BuildBundles.Build -out $out -logFile $log | Out-Null
$code = $LASTEXITCODE
Select-String -Path $log -Pattern "SfMorph bundle|error|Error|not supported because the module" | Select-Object -Last 20 | ForEach-Object { $_.Line }
if ($code -ne 0) { throw "Unity exited with $code (log: $log)" }
if (Select-String -Path $log -Pattern "not supported because the module" -Quiet) { throw "an engine module the bundle needs is disabled in Packages/manifest.json (log: $log)" }
if (-not (Select-String -Path $log -Pattern "AssetBundle Object" -Quiet)) { throw "the bundle has no AssetBundle object (log: $log)" }
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item -Force (Join-Path $out "sfmorph_shaders") $dist
Copy-Item -Force (Join-Path $out "sfmorph_shaders.manifest") $dist
"bundle -> " + (Join-Path $dist "sfmorph_shaders")
