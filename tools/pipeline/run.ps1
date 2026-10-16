<#
SF Body Morphs character pipeline: a VaM character's shape and skin onto her, for SfMorph's Characters gallery.

  powershell -ExecutionPolicy Bypass -File tools\pipeline\run.ps1 -Name Yin `
      -Game "E:\...\SF\VR" -Vam "E:\...\virt\vam" -Blender "E:\...\Blender\blender.exe"

  run.ps1                      newest VaM export -> a character named after it (info.txt), unique ("Name", "Name 2")
  run.ps1 -All                 every export in <Vam>\Saves\PluginData\SfExport that has no converted folder yet
  run.ps1 -All -Force          ... all of them again (existing folders are overwritten)

Paths are remembered in %LOCALAPPDATA%\SfMorph\pipeline.json (-NoSave: not this time), so later runs need nothing.
Inputs: the VaM export(s) above and the newest folder in <Game>\BepInEx\config\SfMorphs\export\ (override with
-VamExport / -GameExport; its textures.txt + PNGs, exported from the Presets tab, give the face-only texture variant
with a blended neck seam: without them that variant is skipped with a WARN). -TextureSize is the body's texture size (default 8192; face and foot get half): albedo, normal and smoothness maps. Output: <Game>\BepInEx\config\SfMorphs\morphs\<Name>\ (the game loads it from there; override
the parent with -OutRoot), with _check\summary.txt (PASS/WARN lines), _check\report.txt and before/after renders.
-All keeps going when a character fails and ends with a table; a failed character has no character.txt, so the game
ignores it and the next -All tries it again.

  run.ps1 -Name Yin -Check     material check only: the character's baked skin against hers (_check\materials.txt,
                               _check\materials.png, 'material check' rows in _check\summary.txt). Nothing is changed.
  run.ps1 -Name Yin -Rebake    bake the textures again on an existing character (no refit; needs its _check\stage.npz),
                               then the material check. The old textures are moved to _check\old_textures first.
  run.ps1 -Sliders             slider fit instead of a conversion: the newest VaM export's (or -VamExport's) relative
                               body proportions as her own Body sliders, written as the preset "<Name> (fit)" in
                               <Game>\BepInEx\config\SfMorphs\presets (Presets tab > Load). Her mesh and skin stay hers.
  run.ps1 -Source <file>       any rigged humanoid instead of a VaM export (DAZ Genesis 2/3/8/9 FBX, Unity / VRChat FBX,
                               Mixamo, VRM, GLB, Character Creator, Unreal; tools/sources/humanoid.py, docs/SOURCES.md):
                               converted to an SfExport folder in %LOCALAPPDATA%\SfMorph\sources\<file name>, then
                               as above (with -Sliders: the slider fit). -Rig <id> forces the bone table, -SourceMesh
                               <object> picks the body mesh; the adapter's report is source.txt in that folder.
  run.ps1 -Daz <file.duf>      a DAZ character (a Genesis 8 / 9 character preset, or a scene) through DAZ Studio:
                               tools/sources/daz_export.dsa loads it at base resolution and writes an FBX with its
                               textures to %LOCALAPPDATA%\SfMorph\daz\<name>, which then goes through -Source.
                               Needs DAZ Studio and the figure's base (e.g. Genesis 9 Starter Essentials) installed.
  -Brightness hers|vam         albedo brightness (default hers: VaM's skin brought to her body's luminance, VaM's colours
                               kept; vam: VaM's values as they are, usually about twice too bright in the game).
#>
param(
    [string]$Name,
    [string]$Game,
    [string]$Vam,
    [string]$Blender,
    [string]$VamExport,
    [string]$GameExport,
    [string]$OutRoot,
    [int]$TextureSize = 8192,
    [switch]$NoBake,
    [switch]$All,
    [switch]$Force,
    [switch]$NoSave,
    [switch]$Check,
    [switch]$Rebake,
    [switch]$Sliders,
    [string]$Source,
    [string]$Daz,
    [string]$Rig = 'auto',
    [string]$SourceMesh,
    [ValidateSet('hers', 'vam')][string]$Brightness = 'hers'
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
if (-not $NoSave) { $cfg | ConvertTo-Json | Set-Content $cfgPath -Encoding UTF8 }
$Game = $cfg.Game; $Vam = $cfg.Vam; $Blender = $cfg.Blender
if ((Split-Path -Leaf $Blender) -ne 'blender.exe') { $Blender = Join-Path $Blender 'blender.exe' }
if (-not (Test-Path $Blender)) { throw "Blender not found: $Blender" }

function Newest($dir) {
    $d = Get-ChildItem -Directory $dir -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $d) { throw "no export folders in $dir" }
    $d.FullName
}
$vamRoot = Join-Path $Vam 'Saves\PluginData\SfExport'
# Her meshes come from the newest export that has her full body (an export made while she's dressed has the outfit's
# body variants instead); her original skin textures from the newest export that has textures.txt (any outfit).
$exportRoot = Join-Path $Game 'BepInEx\config\SfMorphs\export'
$bodyMeshes = 'reika_upperBody_normal.obj', 'reika_lowerBody_normal.obj', 'reika_foot.obj', 'reika_face_a.obj'
# exports made by SfMorph before 0.11 while Physics or Fidelity was on hold our clones ("<mesh> (SfSoft)", subdivided: other
# vertex counts) and our wrinkle / mark render textures instead of her own: never use those
function Test-CloneExport($d) {
    $m = Join-Path $d 'meshes.txt'
    (Test-Path $m) -and (Select-String -Path $m -Pattern '\((SfSoft|SfMorph)\)' -Quiet)
}
function Test-OwnTextures($d) {
    $t = Join-Path $d 'textures.txt'
    (Test-Path $t) -and -not (Select-String -Path $t -Pattern 'RenderTexture' -Quiet)
}
$allExports = Get-ChildItem -Directory $exportRoot -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
$bad = @($allExports | Where-Object { Test-CloneExport $_.FullName })
foreach ($b in $bad) { Write-Host "WARN: her export $($b.Name) holds SfMorph's own mesh clones (Physics / Fidelity were on, SfMorph before 0.11): not used. Export her again with SfMorph 0.11+." }
$usable = @($allExports | Where-Object { -not (Test-CloneExport $_.FullName) })
if (-not $GameExport) {
    # her full body: the body meshes and at least one face (any face type the game has)
    $full = $usable | Where-Object { $d = $_.FullName; -not ($bodyMeshes | Where-Object { $_ -ne 'reika_face_a.obj' -and -not (Test-Path (Join-Path $d $_)) }) -and
        (Get-ChildItem $d -Filter 'reika_face_*.obj' -ErrorAction SilentlyContinue) } | Select-Object -First 1
    # (no usable export: -Daz / -Source still make their source folder; the conversion itself stops below)
    $noExport = -not $full -and -not ($Check -or $Rebake -or $Sliders)
    $GameExport = if ($full) { $full.FullName } else { $null }
}
$texExport = $allExports | Where-Object { Test-OwnTextures $_.FullName } | Select-Object -First 1
if (-not $texExport) {
    $texExport = $allExports | Where-Object { Test-Path (Join-Path $_.FullName 'textures.txt') } | Select-Object -First 1
    if ($texExport) { Write-Host "WARN: her textures come from $($texExport.Name), which holds SfMorph's wrinkle / mark copies (close to hers; export her again with SfMorph 0.11+ for the originals)" }
}
$TextureExport = if ($texExport) { $texExport.FullName } else { $GameExport }
if ($GameExport) {
    $rows = Get-Content (Join-Path $GameExport 'meshes.txt') | Where-Object { $_ -and -not $_.StartsWith('#') }
    if (-not $rows) { throw "meshes.txt in $GameExport lists no meshes: export her again with SfMorph 0.5.1 or newer" }
}
$morphs = if ($OutRoot) { $OutRoot } else { Join-Path $Game 'BepInEx\config\SfMorphs\morphs' }
New-Item -ItemType Directory -Force $morphs | Out-Null

# Blender's own Python has numpy; scipy goes into a private folder (never into Blender's install)
$py = Get-ChildItem (Split-Path $Blender) -Recurse -Filter python.exe -Depth 3 | Where-Object { $_.FullName -match '\\python\\bin\\' } |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $py) { throw "Blender's python.exe not found next to $Blender" }
$deps = Join-Path $store 'pydeps'
$env:PYTHONPATH = $deps
& $py.FullName -c "import scipy" 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Installing scipy for Blender''s Python (once)...'
    & $py.FullName -m pip install --quiet --target $deps scipy
    Get-ChildItem $deps -Directory -Filter 'numpy*' | Remove-Item -Recurse -Force  # keep Blender's own numpy
}

# ---- naming ------------------------------------------------------------------------------------------------
function Get-CharacterName($exportDir) {
    $f = Join-Path $exportDir 'info.txt'
    $n = $null
    if (Test-Path $f) {
        $line = Get-Content $f | Where-Object { $_ -like 'character*' } | Select-Object -First 1
        if ($line) { $n = ($line -split "`t")[1] }
    }
    if ($n) { $n = ($n -replace '[\\/:*?"<>|]', '_').Trim().TrimEnd('.') }
    if (-not $n) { $n = (Split-Path -Leaf $exportDir) }
    $n
}

function Get-CharMeta($dir) {
    $m = @{ exists = $false; vamexport = $null; source = $null }
    $f = Join-Path $dir 'character.txt'
    if (Test-Path $f) {
        $m.exists = $true
        foreach ($l in Get-Content $f) {
            $t = $l -split "`t"
            if ($t.Count -ge 2 -and ($t[0] -eq 'vamexport' -or $t[0] -eq 'source')) { $m[$t[0]] = $t[1] }
        }
    }
    $m
}

# the converted folder of an export: the one whose character.txt names it (vamexport), else (folders made before that
# line existed) one made from a character of the same name
function Find-Converted($leaf, $character) {
    $legacy = $null
    foreach ($d in Get-ChildItem -Directory $morphs -ErrorAction SilentlyContinue) {
        $m = Get-CharMeta $d.FullName
        if (-not $m.exists) { continue }
        if ($m.vamexport -eq $leaf) { return $d.Name }
        if (-not $m.vamexport -and $m.source -and $m.source -eq $character) { $legacy = $d.Name }
    }
    $legacy
}

# a free folder name: "Name", else "Name 2", "Name 3" ... (a folder that already holds this export, or nothing, is free)
function Get-FreeName($base, $leaf, $character) {
    $i = 1
    while ($true) {
        $cand = if ($i -eq 1) { $base } else { "$base $i" }
        $dir = Join-Path $morphs $cand
        if (-not (Test-Path $dir)) { return $cand }
        $m = Get-CharMeta $dir
        if (-not $m.exists) { return $cand }
        if ($m.vamexport -eq $leaf) { return $cand }
        if (-not $m.vamexport -and $m.source -eq $character) { return $cand }
        $i++
    }
}

# ---- one character -----------------------------------------------------------------------------------------
# quiet: only a progress line instead of the whole fit/bake output (the full text goes to _check\run.log either way)
function Convert-Export($exportDir, $charName, $quiet) {
    $res = [pscustomobject]@{ Name = $charName; Export = (Split-Path -Leaf $exportDir); Result = 'FAIL'; Pass = 0; Warn = 0; Note = ''; Out = ''; Flagged = $false }
    $out = Join-Path $morphs $charName
    $res.Out = $out
    try {
        New-Item -ItemType Directory -Force (Join-Path $out '_check') | Out-Null
        $runLog = Join-Path $out '_check\run.log'
        Set-Content $runLog "VaM export : $exportDir`nGame export: $GameExport`nOutput     : $out" -Encoding UTF8
        Get-ChildItem $out -File -Filter '*.morph.txt' -ErrorAction SilentlyContinue | Remove-Item
        foreach ($stale in 'summary.txt', 'stage.npz') { Remove-Item (Join-Path $out "_check\$stale") -ErrorAction SilentlyContinue }
        Write-Host "VaM export : $exportDir`nGame export: $GameExport (textures: $TextureExport)`nOutput     : $out"
        $ErrorActionPreference = 'Continue'   # (Windows PowerShell 5.1 turns native stderr into errors otherwise)
        & $py.FullName (Join-Path $here 'sfpipe.py') --game $GameExport --vam $exportDir --out $out --name $charName 2>&1 | ForEach-Object {
            $t = "$_"; Add-Content $runLog $t -Encoding UTF8
            if (-not $quiet -or $t -match '^(ERROR|WARN|fit stage|character )') { Write-Host $t }
        }
        $fitCode = $LASTEXITCODE
        $summary = Join-Path $out '_check\summary.txt'
        if ($fitCode -ge 2 -or -not (Test-Path (Join-Path $out '_check\stage.npz')) -or -not (Test-Path $summary)) {
            $msg = ''
            if (Test-Path $summary) { $msg = ((Get-Content $summary | Select-Object -First 1) -replace '^FAIL\s+', '') }
            if (-not $msg) { $msg = "fit stage failed (exit $fitCode), see $out\_check\run.log" }
            $res.Note = $msg
            return $res
        }
        $args2 = @('-b', '--factory-startup', '--python-exit-code', '1', '-P', (Join-Path $here 'blend.py'), '--', '--out', $out, '--vam', $exportDir, '--size', $TextureSize)
        if ($NoBake) { $args2 += '--no-bake' }
        $args2 += @('--brightness', $Brightness)
        $env:SFP_GAME_EXPORT = $TextureExport   # bake.py: her original textures (textures.txt) for the face-only seam blend
        & $Blender @args2 2>&1 | ForEach-Object {
            $t = "$_"; Add-Content $runLog $t -Encoding UTF8
            if ($t -match '^\[blend\]' -and (-not $quiet -or $t -match 'WARN|baked|done in')) { Write-Host $t }
        }
        $blenderCode = $LASTEXITCODE
        $lines = @(Get-Content $summary | Where-Object { $_ })
        $res.Pass = @($lines | Where-Object { $_ -like 'PASS*' }).Count
        $res.Warn = @($lines | Where-Object { $_ -like 'WARN*' }).Count
        if ($blenderCode -ne 0) {
            $res.Note = "Blender stage failed (exit $blenderCode), see $out\_check\run.log"
            return $res
        }
        $res.Result = if ($res.Warn -eq 0) { 'OK' } else { 'WARN' }
        $res.Flagged = $fitCode -ne 0
        $first = $lines | Where-Object { $_ -like 'WARN*' } | Select-Object -First 1
        if ($first) { $res.Note = (($first -replace '^WARN\s+', '')); if ($res.Warn -gt 1) { $res.Note += " (+$($res.Warn - 1) more)" } }
    } catch {
        $res.Note = "$($_.Exception.Message)"
    } finally {
        $ErrorActionPreference = 'Stop'
    }
    $res
}

function Show-Summary($res) {
    $s = Join-Path $res.Out '_check\summary.txt'
    Write-Host ''
    if (Test-Path $s) { Write-Host "--- $($res.Name): _check\summary.txt ---"; Get-Content $s | ForEach-Object { Write-Host $_ } }
    else { Write-Host "--- $($res.Name): no summary ($($res.Note)) ---" }
}

# -Check / -Rebake on an existing character
function Update-Textures($charName, $checkOnly) {
    $out = Join-Path $morphs $charName
    if (-not (Test-Path (Join-Path $out 'character.txt'))) { throw "no converted character '$charName' in $morphs" }
    $env:SFP_GAME_EXPORT = $TextureExport
    $ErrorActionPreference = 'Continue'
    try {
        if ($checkOnly) {
            & $Blender -b --factory-startup --python-exit-code 1 -P (Join-Path $here 'matcheck.py') -- --out $out --her "$TextureExport" 2>&1 |
                ForEach-Object { $t = "$_"; if ($t -match '^\[matcheck\]') { Write-Host $t } }
            if ($LASTEXITCODE -ne 0) { throw "material check failed (exit $LASTEXITCODE)" }
        } else {
            $meta = Get-CharMeta $out
            $vexp = if ($VamExport) { $VamExport } elseif ($meta.vamexport) { Join-Path $vamRoot $meta.vamexport } else { $null }
            if (-not $vexp -or -not (Test-Path $vexp)) { throw "the VaM export of '$charName' ($($meta.vamexport)) is not in $vamRoot (give -VamExport)" }
            if (-not (Test-Path (Join-Path $out '_check\stage.npz'))) { throw "'$charName' has no _check\stage.npz: convert it again (run.ps1 -VamExport ... -Name '$charName')" }
            # the first re-bake keeps the textures it replaces (the pipeline's earlier bake); later ones just bake again
            $old = Join-Path $out '_check\old_textures'
            if (-not (Get-ChildItem $old -File -Filter 'tex_*' -ErrorAction SilentlyContinue)) {
                New-Item -ItemType Directory -Force $old | Out-Null
                Get-ChildItem $out -File -Filter 'tex_*' | Move-Item -Destination $old -Force
                Write-Host "Old textures moved to $old"
            }
            $runLog = Join-Path $out '_check\run.log'
            Add-Content $runLog "`n--- rebake $(Get-Date -Format s): VaM export $vexp, her textures $TextureExport, brightness $Brightness" -Encoding UTF8
            & $Blender -b --factory-startup --python-exit-code 1 -P (Join-Path $here 'blend.py') -- --out $out --vam $vexp --size $TextureSize --bake-only --brightness $Brightness 2>&1 |
                ForEach-Object { $t = "$_"; Add-Content $runLog $t -Encoding UTF8; if ($t -match '^\[(blend|matcheck)\]') { Write-Host $t } }
            if ($LASTEXITCODE -ne 0) { throw "Blender stage failed (exit $LASTEXITCODE), see $runLog" }
        }
    } finally { $ErrorActionPreference = 'Stop' }
    Show-Summary ([pscustomobject]@{ Name = $charName; Out = $out; Note = '' })
    Write-Host "Contact sheet: $out\_check\materials.png   numbers: $out\_check\materials.txt"
    if (-not $checkOnly) { Write-Host 'In the game: F8 > Characters > Rescan (or pick the character again).' }
}

# ---- run ---------------------------------------------------------------------------------------------------
if ($Daz) {
    # a DAZ character -> FBX through DAZ Studio (tools/sources/daz_export.dsa), then -Source
    if (-not (Test-Path $Daz)) { throw "-Daz: no file $Daz" }
    $ds = $null
    try { $ds = (Get-ItemProperty 'HKCU:\Software\DAZ\Studio6' -ErrorAction Stop).'InstallPath-64' } catch {}
    if (-not $ds) { try { $ds = (Get-ItemProperty 'HKCU:\Software\DAZ\Studio4' -ErrorAction Stop).'InstallPath-64' } catch {} }
    $exe = if ($ds) { Join-Path $ds 'DAZStudio.exe' } else { $null }
    if (-not $exe -or -not (Test-Path $exe)) { throw 'DAZ Studio not found (registry HKCU\Software\DAZ\Studio6 InstallPath-64)' }
    # the figure the preset needs (its first node's .dsf, e.g. /data/Daz 3D/Genesis 9/Base/Genesis9.dsf) must be in a DAZ
    # content library, else DAZ Studio stops on its modal missing-files dialog
    $libs = @()
    try {
        $reg = Get-ItemProperty 'HKCU:\Software\DAZ\Studio6' -ErrorAction Stop
        for ($i = 0; $i -lt [int]$reg.NumContentDirs; $i++) { $libs += $reg."ContentDir$i" }
    } catch {}
    try {
        $fs = [IO.File]::OpenRead($Daz)
        $gz = New-Object IO.Compression.GZipStream($fs, [IO.Compression.CompressionMode]::Decompress)
        $json = (New-Object IO.StreamReader($gz)).ReadToEnd(); $fs.Close()
    } catch { $json = Get-Content $Daz -Raw }
    $m = [regex]::Match($json, '"url"\s*:\s*"(/data/[^"#]+\.dsf)#')
    if ($m.Success -and $libs) {
        $need = [uri]::UnescapeDataString($m.Groups[1].Value).TrimStart('/')
        $have = $libs | Where-Object { Test-Path (Join-Path $_ $need) }
        if (-not $have) {
            Write-Host "FAILED: the character needs $need, which is in none of DAZ Studio's content libraries ($($libs -join '; ')). Install its base figure first (Genesis 9: Genesis 9 Starter Essentials, free, through Daz Install Manager or Daz Connect)."
            exit 1
        }
    }
    $leaf = [IO.Path]::GetFileNameWithoutExtension($Daz) -replace '[\\/:*?"<>|]', '_'
    $fbx = Join-Path $store "daz\$leaf\$leaf.fbx"
    New-Item -ItemType Directory -Force (Split-Path $fbx) | Out-Null
    Remove-Item "$fbx.status.txt" -ErrorAction SilentlyContinue
    $dsa = (Resolve-Path (Join-Path $here '..\sources\daz_export.dsa')).Path
    Write-Host "DAZ Studio : $exe`nCharacter  : $Daz`nFBX        : $fbx  (DAZ Studio opens, exports and closes)"
    # DAZ Studio runs one instance: a second launch hands over nothing and quits
    if (Get-Process DAZStudio -ErrorAction SilentlyContinue) { Write-Host 'FAILED: DAZ Studio is already running: close it (save your scene first) and run this again'; exit 1 }
    $proc = Start-Process -FilePath $exe -ArgumentList @('-scriptArg', "`"$Daz`"", '-scriptArg', "`"$fbx`"", "`"$dsa`"") -PassThru
    # a modal dialog (missing files, a login prompt) would keep DAZ Studio open: give up after 10 minutes without a status file
    $t0 = Get-Date
    while (-not $proc.HasExited -and -not (Test-Path "$fbx.status.txt") -and ((Get-Date) - $t0).TotalMinutes -lt 10) { Start-Sleep -Seconds 2 }
    if (-not $proc.HasExited -and -not (Test-Path "$fbx.status.txt")) {
        Write-Host 'FAILED: DAZ Studio did not finish in 10 minutes (a dialog is probably open in its window: missing files, login). Its log: %APPDATA%\DAZ 3D\Studio6\log.txt'
        exit 1
    }
    # after the export DAZ Studio closes its window; it can linger in the background (seen with DAZ Studio 6): end the instance
    # this run started once the status is written and it has had 90 s to quit
    if (-not $proc.HasExited) { $proc.WaitForExit(90000) | Out-Null }
    if (-not $proc.HasExited) { Write-Host 'DAZ Studio did not quit after the export: ending the instance this run started'; Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    $st = if (Test-Path "$fbx.status.txt") { (Get-Content "$fbx.status.txt" -Raw).Trim() } else { 'FAIL no status file (the script did not run: check DAZ Studio''s log, Help > Troubleshooting > View Log File)' }
    Write-Host "DAZ export : $st"
    if (-not $st.StartsWith('OK') -or -not (Test-Path $fbx)) { exit 1 }
    $Source = $fbx
}
if ($Source) {
    # any rigged humanoid -> an SfExport folder (tools/sources/humanoid.py), then the usual steps on it
    if (-not (Test-Path $Source)) { throw "-Source: no file $Source" }
    $leaf = [IO.Path]::GetFileNameWithoutExtension($Source) -replace '[\\/:*?"<>|]', '_'
    $srcOut = Join-Path $store "sources\$leaf"
    Write-Host "Source     : $Source -> $srcOut"
    $a3 = @('-b', '--factory-startup', '--python-exit-code', '1', '-P', (Join-Path $here '..\sources\humanoid.py'), '--', '--in', $Source, '--out', $srcOut, '--rig', $Rig)
    if ($Name) { $a3 += @('--name', $Name) }
    if ($SourceMesh) { $a3 += @('--mesh', $SourceMesh) }
    $ErrorActionPreference = 'Continue'
    & $Blender @a3 2>&1 | ForEach-Object { $t = "$_"; if ($t -match '^\[humanoid\]|Error|Traceback') { Write-Host $t } }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($code -ne 0 -or -not (Test-Path (Join-Path $srcOut 'body.obj'))) { Write-Host "FAILED: the source adapter (exit $code); see the lines above"; exit 1 }
    $VamExport = $srcOut
}
if ($Sliders) {
    if (-not $VamExport) { $VamExport = Newest $vamRoot }
    # any export of her has her shape (a clone export too: the same surface, only more vertices)
    $her = if ($GameExport) { $GameExport } else { ($allExports | Where-Object { Test-Path (Join-Path $_.FullName 'skeleton.txt') } | Select-Object -First 1).FullName }
    if (-not $her) { throw "no export of her in $exportRoot (Presets tab > Export)" }
    $character = Get-CharacterName $VamExport
    if (-not $Name) { $Name = $character }
    $preset = Join-Path $Game ("BepInEx\config\SfMorphs\presets\$Name (fit).txt")
    Write-Host "VaM export : $VamExport`nHer export : $her`nPreset     : $preset"
    $ErrorActionPreference = 'Continue'
    & $py.FullName (Join-Path $here 'sliderfit.py') --game $her --vam $VamExport --out $preset --name $character 2>&1 | ForEach-Object { Write-Host "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($code -ne 0) { Write-Host "FAILED: slider fit (exit $code)"; exit 1 }
    Write-Host "In the game: F8 > Presets > '$Name (fit)' > Load (it replaces the Body sliders; tune by hand from there)."
    exit 0
}
if ($noExport) { throw "no usable export of her in $exportRoot (Presets tab > Export, SfMorph 0.11+)" }
if ($Check -or $Rebake) {
    if (-not $Name) { throw "-Check / -Rebake need -Name (the converted character's folder name in $morphs)" }
    Update-Textures $Name $Check
    exit 0
}
if ($All) {
    if ($Name) { Write-Host '-Name is ignored with -All (each character is named from its info.txt)' }
    if (-not (Test-Path $vamRoot)) { throw "no SfExport folder in $vamRoot (export a character from VaM with the SfExport plugin first)" }
    $exports = @(Get-ChildItem -Directory $vamRoot | Sort-Object LastWriteTime)
    if (-not $exports) { throw "no export folders in $vamRoot" }
    $table = @()
    $n = 0
    foreach ($e in $exports) {
        $n++
        $character = Get-CharacterName $e.FullName
        $done = Find-Converted $e.Name $character
        if ($done -and -not $Force) {
            Write-Host "[$n/$($exports.Count)] $($e.Name): already converted as '$done' (-Force converts it again)"
            $table += [pscustomobject]@{ Name = $done; Export = $e.Name; Result = 'skipped'; Pass = ''; Warn = ''; Note = '' }
            continue
        }
        $target = if ($done) { $done } else { Get-FreeName $character $e.Name $character }
        Write-Host ''
        Write-Host "[$n/$($exports.Count)] $($e.Name) -> '$target'"
        $res = Convert-Export $e.FullName $target $true
        Show-Summary $res
        $table += $res
    }
    Write-Host ''
    $table | Format-Table Name, Export, Result, Pass, Warn, Note -AutoSize -Wrap | Out-String -Width 200 | Write-Host
    $failed = @($table | Where-Object { $_.Result -eq 'FAIL' }).Count
    Write-Host ("{0} export(s): {1} converted, {2} skipped, {3} failed. Open F8 > Characters > Rescan in the game." -f $table.Count,
        @($table | Where-Object { $_.Result -eq 'OK' -or $_.Result -eq 'WARN' }).Count, @($table | Where-Object { $_.Result -like 'skipped*' }).Count, $failed)
    if ($failed) { exit 1 }
} else {
    if (-not $VamExport) { $VamExport = Newest $vamRoot }
    $character = Get-CharacterName $VamExport
    $leaf = Split-Path -Leaf $VamExport
    if (-not $Name) { $Name = Get-FreeName $character $leaf $character }
    $res = Convert-Export $VamExport $Name $false
    Show-Summary $res
    Write-Host ''
    if ($res.Result -eq 'FAIL') {
        Write-Host "FAILED: '$Name': $($res.Note)"
        exit 1
    }
    Write-Host ("Done: '$Name' in $($res.Out)" + $(if ($res.Flagged) { '  (verification flagged rows: see _check\report.txt)' } else { '' }))
    Write-Host "Renders: $($res.Out)\_check\*.png"
    Write-Host 'In the game: F8 > Characters > Rescan.'
}
