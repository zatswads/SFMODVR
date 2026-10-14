<#
SF Body Morphs character pipeline: a VaM character's shape and skin onto her, for SfMorph's Characters gallery.

  powershell -ExecutionPolicy Bypass -File tools\pipeline\run.ps1 -Name Yin `
      -Game "E:\...\SF\VR" -Vam "E:\...\virt\vam" -Blender "E:\...\Blender\blender.exe"

Paths are remembered in %LOCALAPPDATA%\SfMorph\pipeline.json, so later runs only need -Name (or nothing).
Inputs: the newest folder in <Vam>\Saves\PluginData\SfExport\ and in <Game>\BepInEx\config\SfMorphs\export\
(override with -VamExport / -GameExport). Output: <Game>\BepInEx\config\SfMorphs\morphs\<Name>\ (the game
loads it from there), with _check\report.txt and before/after renders.
#>
param(
    [string]$Name,
    [string]$Game,
    [string]$Vam,
    [string]$Blender,
    [string]$VamExport,
    [string]$GameExport,
    [int]$TextureSize = 4096,
    [switch]$NoBake
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$store = Join-Path $env:LOCALAPPDATA 'SfMorph'
$cfgPath = Join-Path $store 'pipeline.json'
New-Item -ItemType Directory -Force $store | Out-Null
$cfg = @{}
if (Test-Path $cfgPath) { (Get-Content $cfgPath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $cfg[$_.Name] = $_.Value } }
foreach ($k in 'Game', 'Vam', 'Blender') {
    $v = Get-Variable $k -ValueOnly
    if ($v) { $cfg[$k] = $v } elseif (-not $cfg[$k]) { throw "-$k is needed once (then remembered in $cfgPath)" }
}
$cfg | ConvertTo-Json | Set-Content $cfgPath -Encoding UTF8
$Game = $cfg.Game; $Vam = $cfg.Vam; $Blender = $cfg.Blender
if ((Split-Path -Leaf $Blender) -ne 'blender.exe') { $Blender = Join-Path $Blender 'blender.exe' }
if (-not (Test-Path $Blender)) { throw "Blender not found: $Blender" }

function Newest($dir) {
    $d = Get-ChildItem -Directory $dir -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $d) { throw "no export folders in $dir" }
    $d.FullName
}
if (-not $VamExport) { $VamExport = Newest (Join-Path $Vam 'Saves\PluginData\SfExport') }
if (-not $GameExport) { $GameExport = Newest (Join-Path $Game 'BepInEx\config\SfMorphs\export') }
$rows = Get-Content (Join-Path $GameExport 'meshes.txt') | Where-Object { $_ -and -not $_.StartsWith('#') }
if (-not $rows) { throw "meshes.txt in $GameExport lists no meshes: export her again with SfMorph 0.5.1 or newer" }
if (-not $Name) {
    $info = Get-Content (Join-Path $VamExport 'info.txt') | Where-Object { $_ -like 'character*' } | Select-Object -First 1
    $Name = if ($info) { ($info -split "`t")[1] } else { 'Character' }
}
$out = Join-Path $Game "BepInEx\config\SfMorphs\morphs\$Name"
New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem $out -File -Filter '*.morph.txt' -ErrorAction SilentlyContinue | Remove-Item

# Blender's own Python has numpy; scipy goes into a private folder (never into Blender's install)
$py = Get-ChildItem (Split-Path $Blender) -Recurse -Filter python.exe -Depth 3 | Where-Object { $_.FullName -match '\\python\\bin\\' } | Select-Object -First 1
if (-not $py) { throw "Blender's python.exe not found next to $Blender" }
$deps = Join-Path $store 'pydeps'
$env:PYTHONPATH = $deps
& $py.FullName -c "import scipy" 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Installing scipy for Blender''s Python (once)...'
    & $py.FullName -m pip install --quiet --target $deps scipy
    Get-ChildItem $deps -Directory -Filter 'numpy*' | Remove-Item -Recurse -Force  # keep Blender's own numpy
}

Write-Host "VaM export : $VamExport`nGame export: $GameExport`nOutput     : $out"
& $py.FullName (Join-Path $here 'sfpipe.py') --game $GameExport --vam $VamExport --out $out --name $Name
$fitOk = $LASTEXITCODE -eq 0
$args2 = @('-b', '--factory-startup', '-P', (Join-Path $here 'blend.py'), '--', '--out', $out, '--vam', $VamExport, '--size', $TextureSize)
if ($NoBake) { $args2 += '--no-bake' }
& $Blender @args2 | Select-String '^\[blend\]' | ForEach-Object { $_.Line }
if ($LASTEXITCODE -ne 0) { throw 'Blender stage failed (see output above)' }
Write-Host ''
Write-Host ("Done: '$Name' in $out" + $(if ($fitOk) { '' } else { '  (verification flagged rows: see _check\report.txt)' }))
Write-Host "Renders: $out\_check\*.png"
