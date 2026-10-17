<#
SF Avatar Import: any character source -> an avatar for SF Body Morphs' avatar replacement mode (docs/SOURCES.md), in one go.
Double-click "SF Avatar Import.cmd" (next to this script) for the window, or run it without a window:

  powershell -ExecutionPolicy Bypass -File tools\avatar\import.ps1 -Source <zip | var | folder | file> [-Name <Name>] [-Look <text>]
             [-Install] [-KeepVam] [-NoGui]

Sources (a .zip is unpacked first and its content looked at the same way):
  VaM      a .var package, a folder or zip with VaM content (Custom\..., Saves\scene\...), an appearance preset (.vap) or a scene
           (.json): every appearance preset and every Person of every scene in it is a look. VaM itself makes the export
           (morphs, textures and skin weights as VaM has them): the importer installs tools\vam\SfExport.cs and the small
           BepInEx helper tools\vam\SfVamBridge into VaM, starts VaM in desktop mode, VaM loads each look, exports it and
           closes (-KeepVam: stays open). Then tools\avatar\vam_avatar.py makes the FBX.
  SfExport a folder SfExport.cs wrote (body.obj, joints.txt, weights.txt): straight to the FBX.
  DAZ      a .duf character preset, or a DAZ product zip / folder (data\, People\ ...: copied into your first DAZ content library,
           files that are there already are kept): DAZ Studio exports the figure (tools\sources\daz_export.dsa).
  FBX      a rigged DAZ Genesis 9 / 8 / 2 figure as .fbx (with <fbx>.materials.txt and textures next to it if DAZ wrote them).
-Look picks looks whose name contains the text (default: all looks in the source; DAZ: the first character preset).
Then tools\avatar\build.ps1 builds the bundle into %LOCALAPPDATA%\SfMorph\avatars\<Name>; -Install also copies it into the
game (<Game>\BepInEx\config\SfMorphs\avatars\<Name>: the Characters tab's Avatars list).

Software (found automatically where possible, else set once in the window; remembered in %LOCALAPPDATA%\SfMorph\pipeline.json,
shared with tools\pipeline\run.ps1): Unity 2022.3.9f1 (the game's version, through Unity Hub) and Blender 4 or later always;
VaM (with BepInEx 5) for VaM looks; DAZ Studio 4.2x / 2025 with the figure's base for DAZ characters; the game folder to install.
Personal use: the output contains VaM / DAZ content; never share it.
#>
param(
    [string]$Source,
    [string]$Name,
    [string]$Look,
    [switch]$Install,
    [switch]$KeepVam,
    [switch]$NoGui
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$store = Join-Path $env:LOCALAPPDATA 'SfMorph'
$cfgPath = Join-Path $store 'pipeline.json'
New-Item -ItemType Directory -Force $store | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

# ---- settings: the software and folders this needs ------------------------------------------------------------------------
function Get-Cfg {
    $c = @{}
    if (Test-Path $cfgPath) { try { (Get-Content $cfgPath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $c[$_.Name] = $_.Value } } catch {} }
    return $c
}
function Save-Cfg($c) { $c | ConvertTo-Json | Set-Content $cfgPath -Encoding UTF8 }

function Find-Blender {
    $c = @()
    foreach ($k in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*') {
        Get-ItemProperty $k -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'Blender*' -and $_.InstallLocation } | ForEach-Object { $c += Join-Path $_.InstallLocation 'blender.exe' }
    }
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($root -and (Test-Path "$root\Blender Foundation")) { Get-ChildItem "$root\Blender Foundation" -Directory | Sort-Object Name -Descending | ForEach-Object { $c += Join-Path $_.FullName 'blender.exe' } }
    }
    # Steam libraries
    $steam = $null; try { $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath } catch {}
    if ($steam) {
        $libs = @($steam)
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) { [regex]::Matches((Get-Content $vdf -Raw), '"path"\s*"([^"]+)"') | ForEach-Object { $libs += $_.Groups[1].Value -replace '\\\\', '\' } }
        foreach ($l in $libs) { $c += Join-Path $l 'steamapps\common\Blender\blender.exe' }
    }
    return $c | Where-Object { Test-Path $_ } | Select-Object -First 1
}
function Find-Unity {
    $c = @("$env:ProgramFiles\Unity\Hub\Editor\2022.3.9f1\Editor\Unity.exe")
    $hub = Join-Path $env:APPDATA 'UnityHub\secondaryInstallPath.json'
    if (Test-Path $hub) { try { $p = (Get-Content $hub -Raw | ConvertFrom-Json); if ($p) { $c += Join-Path $p '2022.3.9f1\Editor\Unity.exe' } } catch {} }
    return $c | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
function Find-Daz {
    foreach ($k in 'HKCU:\Software\DAZ\Studio6', 'HKCU:\Software\DAZ\Studio4') {
        try { $p = (Get-ItemProperty $k -ErrorAction Stop).'InstallPath-64'; if ($p -and (Test-Path (Join-Path $p 'DAZStudio.exe'))) { return (Join-Path $p 'DAZStudio.exe') } } catch {}
    }
    return $null
}
function Get-DazLibraries {
    $libs = @()
    foreach ($k in 'HKCU:\Software\DAZ\Studio6', 'HKCU:\Software\DAZ\Studio4') {
        try { $reg = Get-ItemProperty $k -ErrorAction Stop; for ($i = 0; $i -lt [int]$reg.NumContentDirs; $i++) { $libs += $reg."ContentDir$i" } } catch {}
        if ($libs) { break }
    }
    return $libs
}
function Resolve-Tools($c) {
    if (-not $c.Blender -or -not (Test-Path $c.Blender)) { $b = Find-Blender; if ($b) { $c.Blender = $b } }
    if ($c.Blender -and (Split-Path -Leaf $c.Blender) -ne 'blender.exe') { $c.Blender = Join-Path $c.Blender 'blender.exe' }
    if (-not $c.Unity -or -not (Test-Path $c.Unity)) { $u = Find-Unity; if ($u) { $c.Unity = $u } }
    if (-not $c.Daz -or -not (Test-Path $c.Daz)) { $d = Find-Daz; if ($d) { $c.Daz = $d } }
    return $c
}

# ---- what a source is ---------------------------------------------------------------------------------------------------
function Safe([string]$s) { ($s -replace '[\\/:*?"<>|]', '_').Trim() }

function Expand-Zip([string]$zip, [string]$dst) {
    if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
    New-Item -ItemType Directory -Force $dst | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $dst)
}

# a look of a VaM source: the preset path VaM loads (VaM-relative or Package:/...) and its label
function New-VamLook($path, $label) { [pscustomobject]@{ Path = $path; Label = (Safe $label) } }

# the Person atoms of a scene as appearance presets (VaM loads only the appearance part of a preset's storables)
function Convert-ScenePersons([string]$json, [string]$pkg, [string]$sceneName, [string]$vam) {
    $looks = @()
    try { $scene = $json | ConvertFrom-Json } catch { Write-Host "  (scene $sceneName is not readable JSON: skipped)"; return @() }
    $persons = @($scene.atoms | Where-Object { $_.type -eq 'Person' })
    foreach ($p in $persons) {
        $label = if ($persons.Count -gt 1) { "$sceneName $($p.id)" } else { $sceneName }
        $vap = [ordered]@{ setUnlistedParamsToDefault = 'true'; storables = @($p.storables | Where-Object { $_.id -ne 'PluginManager' -and $_.id -notlike 'plugin#*' }) }
        $text = $vap | ConvertTo-Json -Depth 100 -Compress
        if ($pkg) { $text = $text.Replace('SELF:/', "$pkg`:/") }
        $dir = Join-Path $vam 'Custom\Atom\Person\Appearance\SfImport'
        New-Item -ItemType Directory -Force $dir | Out-Null
        $f = "Preset_$(Safe $label).vap"
        [IO.File]::WriteAllText((Join-Path $dir $f), $text, (New-Object Text.UTF8Encoding $false))
        $looks += New-VamLook "Custom/Atom/Person/Appearance/SfImport/$f" $label
    }
    return $looks
}

# a .var: into AddonPackages (VaM resolves Package:/ paths from there), its appearance presets and scene Persons as looks
function Get-VarLooks([string]$var, [string]$vam) {
    $pkg = [IO.Path]::GetFileNameWithoutExtension($var)
    $dst = Join-Path $vam "AddonPackages\$pkg.var"
    $already = Get-ChildItem (Join-Path $vam 'AddonPackages') -Recurse -Filter "$pkg.var" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $already) { Copy-Item -LiteralPath $var $dst; Write-Host "  installed $pkg.var into VaM's AddonPackages" }
    $zip = [IO.Compression.ZipFile]::OpenRead($var)
    $looks = @()
    try {
        $meta = $zip.Entries | Where-Object { $_.FullName -eq 'meta.json' } | Select-Object -First 1
        if ($meta) {
            $r = New-Object IO.StreamReader($meta.Open()); $mj = $r.ReadToEnd(); $r.Close()
            try {
                $deps = @(([regex]::Matches($mj, '"([^"/\\]+\.[^"/\\]+\.(\d+|latest))"\s*:\s*\{')) | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
                $have = @{}
                Get-ChildItem (Join-Path $vam 'AddonPackages') -Recurse -Filter *.var -ErrorAction SilentlyContinue | ForEach-Object { $have[[IO.Path]::GetFileNameWithoutExtension($_.Name)] = 1; $have[($_.BaseName -replace '\.\d+$', '.latest')] = 1 }
                $miss = @($deps | Where-Object { -not $have.ContainsKey($_) -and -not $have.ContainsKey(($_ -replace '\.\d+$', '.latest')) })
                if ($miss) { Write-Host "  WARNING: packages it depends on are not in VaM (textures / morphs from them will be missing): $($miss -join ', ')" }
            } catch {}
        }
        foreach ($e in $zip.Entries) {
            $n = $e.FullName -replace '\\', '/'
            if ($n -match '^Custom/Atom/Person/Appearance/.+\.vap$') { $looks += New-VamLook "$pkg`:/$n" ([IO.Path]::GetFileNameWithoutExtension($n) -replace '^Preset_', '') }
        }
        foreach ($e in $zip.Entries) {
            $n = $e.FullName -replace '\\', '/'
            if ($n -match '^Saves/scene/.+\.json$') {
                $r = New-Object IO.StreamReader($e.Open()); $j = $r.ReadToEnd(); $r.Close()
                $looks += Convert-ScenePersons $j $pkg ([IO.Path]::GetFileNameWithoutExtension($n)) $vam
            }
        }
    } finally { $zip.Dispose() }
    return $looks
}

# a folder with loose VaM content (Custom\, Saves\): merged into VaM (existing files kept), its presets and scenes as looks
function Get-FolderVamLooks([string]$dir, [string]$vam) {
    $looks = @()
    $root = $dir
    $c = Get-ChildItem $dir -Recurse -Directory -Filter Custom -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($c) { $root = Split-Path $c.FullName }
    foreach ($f in Get-ChildItem $root -Recurse -File) {
        $rel = $f.FullName.Substring($root.Length).TrimStart('\')
        if ($rel -notmatch '^(Custom|Saves)\\') { continue }
        $dst = Join-Path $vam $rel
        if (-not (Test-Path -LiteralPath $dst)) { New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null; Copy-Item -LiteralPath $f.FullName $dst }
        $r = $rel -replace '\\', '/'
        if ($r -match '^Custom/Atom/Person/Appearance/.+\.vap$') { $looks += New-VamLook $r ([IO.Path]::GetFileNameWithoutExtension($r) -replace '^Preset_', '') }
        elseif ($r -match '^Saves/scene/.+\.json$') { $looks += Convert-ScenePersons (Get-Content -LiteralPath $f.FullName -Raw) $null ([IO.Path]::GetFileNameWithoutExtension($r)) $vam }
    }
    return $looks
}

# Classifies a source; returns @{ Kind = sfexport|fbx|daz|vam; ... }
function Get-SourceKind([string]$src, [string]$work, $cfg) {
    $item = Get-Item -LiteralPath $src
    if (-not $item.PSIsContainer) {
        switch ($item.Extension.ToLowerInvariant()) {
            '.var' { if (-not $cfg.Vam) { throw 'a VaM package needs the VaM folder (set it in the window or pipeline.json)' }; return @{ Kind = 'vam'; Looks = @(Get-VarLooks $item.FullName $cfg.Vam) } }
            '.zip' {
                $x = Join-Path $work 'src'
                Write-Host "unpacking $($item.Name) ..."
                Expand-Zip $item.FullName $x
                return Get-SourceKind $x $work $cfg
            }
            '.fbx' { return @{ Kind = 'fbx'; Fbx = $item.FullName } }
            '.duf' { return @{ Kind = 'daz'; Duf = @($item.FullName) } }
            '.vap' {
                if (-not $cfg.Vam) { throw 'a VaM preset needs the VaM folder' }
                $full = $item.FullName; $v = (Resolve-Path $cfg.Vam).Path.TrimEnd('\')
                if ($full.StartsWith($v + '\', [StringComparison]::OrdinalIgnoreCase)) { $p = $full.Substring($v.Length + 1) -replace '\\', '/' }
                else {
                    $d = Join-Path $cfg.Vam 'Custom\Atom\Person\Appearance\SfImport'; New-Item -ItemType Directory -Force $d | Out-Null
                    Copy-Item -LiteralPath $full (Join-Path $d $item.Name) -Force; $p = "Custom/Atom/Person/Appearance/SfImport/$($item.Name)"
                }
                return @{ Kind = 'vam'; Looks = @(New-VamLook $p ($item.BaseName -replace '^Preset_', '')) }
            }
            '.json' {
                if (-not $cfg.Vam) { throw 'a VaM scene needs the VaM folder' }
                return @{ Kind = 'vam'; Looks = @(Convert-ScenePersons (Get-Content -LiteralPath $item.FullName -Raw) $null $item.BaseName $cfg.Vam) }
            }
            default { throw "don't know what to do with a $($item.Extension) file: give a .zip, .var, .vap, .json scene, .duf, .fbx or a folder" }
        }
    }
    $dir = $item.FullName
    # an SfExport folder (possibly one level down)
    $obj = Get-ChildItem $dir -Recurse -Depth 2 -Filter body.obj -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.DirectoryName 'joints.txt') } | Select-Object -First 1
    if ($obj) { return @{ Kind = 'sfexport'; Dir = $obj.DirectoryName } }
    # VaM packages inside (a zip of .var files)
    $vars = @(Get-ChildItem $dir -Recurse -Filter *.var -ErrorAction SilentlyContinue)
    if ($vars) {
        if (-not $cfg.Vam) { throw 'VaM packages need the VaM folder' }
        $looks = @(); foreach ($v in $vars) { $looks += Get-VarLooks $v.FullName $cfg.Vam }
        return @{ Kind = 'vam'; Looks = $looks }
    }
    # an unpacked .var or loose VaM content
    if ((Test-Path (Join-Path $dir 'meta.json')) -or (Get-ChildItem $dir -Recurse -Directory -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\Custom\\Atom\\Person\\Appearance$|\\Saves\\scene$' } | Select-Object -First 1)) {
        if (-not $cfg.Vam) { throw 'VaM content needs the VaM folder' }
        return @{ Kind = 'vam'; Looks = @(Get-FolderVamLooks $dir $cfg.Vam) }
    }
    # DAZ content (data\, People\ ...), or loose .duf files
    $dsf = Get-ChildItem $dir -Recurse -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -in 'data', 'People' } | Select-Object -First 1
    $dufs = @(Get-ChildItem $dir -Recurse -Filter *.duf -ErrorAction SilentlyContinue)
    if ($dsf -or $dufs) {
        if ($dsf) {
            $root = Split-Path $dsf.FullName
            $libs = Get-DazLibraries
            if (-not $libs) { throw 'DAZ content, but no DAZ Studio content library found (install DAZ Studio first)' }
            $lib = $libs[0]; $n = 0
            foreach ($f in Get-ChildItem $root -Recurse -File) {
                $dst = Join-Path $lib $f.FullName.Substring($root.Length).TrimStart('\')
                if (-not (Test-Path -LiteralPath $dst)) { New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null; Copy-Item -LiteralPath $f.FullName $dst; $n++ }
            }
            Write-Host "  copied $n DAZ content files into $lib"
            # the product's presets, at their place in the library (DAZ Studio resolves their /data/... references from there)
            $dufs = @(Get-ChildItem $root -Recurse -Filter *.duf | ForEach-Object { Get-Item -LiteralPath (Join-Path $lib $_.FullName.Substring($root.Length).TrimStart('\')) })
        }
        # character presets first (People\...\Characters\...), then anything else
        $chars = @($dufs | Where-Object { $_.FullName -match '\\Characters?\\' -and $_.FullName -notmatch '\\(Materials?|Poses?|Expressions?|Shaping|Morphs|Hair|Clothing|Wardrobe)\\' })
        if (-not $chars) { $chars = @($dufs | Where-Object { $_.FullName -notmatch '\\(Materials?|Poses?|Expressions?)\\' }) }
        if (-not $chars) { throw 'DAZ content without a character preset (.duf) to load' }
        return @{ Kind = 'daz'; Duf = @($chars | ForEach-Object { $_.FullName }) }
    }
    $fbx = Get-ChildItem $dir -Recurse -Filter *.fbx -ErrorAction SilentlyContinue | Sort-Object Length -Descending | Select-Object -First 1
    if ($fbx) { return @{ Kind = 'fbx'; Fbx = $fbx.FullName } }
    throw "no character found in $dir (looked for: VaM .var / presets / scenes, an SfExport folder, DAZ content, an .fbx)"
}

# ---- steps ----------------------------------------------------------------------------------------------------------------
function Invoke-Blender($cfg, [string[]]$a) {
    if (-not $cfg.Blender -or -not (Test-Path $cfg.Blender)) { throw 'Blender not found: set it in the window (blender.exe)' }
    # native stderr (Blender's warnings) must not stop the script: Windows PowerShell turns it into errors under 'Stop'
    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & $cfg.Blender -b --factory-startup --python-exit-code 1 @a 2>&1 | ForEach-Object { $l = "$_"; if ($l -match '^\[vam_avatar\]|FAILED|Error:|Traceback') { Write-Host "  $l" } }
    $code = $LASTEXITCODE; $ErrorActionPreference = $eap
    if ($code -ne 0) { throw "Blender failed (exit $code)" }
}

function Invoke-Build($cfg, [string]$fbx, [string]$name, [bool]$install) {
    if (-not $cfg.Unity -or -not (Test-Path $cfg.Unity)) { throw 'Unity 2022.3.9f1 not found: install it through Unity Hub (the game''s version), or set Unity.exe in the window' }
    Write-Host "building the avatar bundle (Unity, a few minutes) ..."
    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here 'build.ps1') -Fbx $fbx -Name $name -Unity $cfg.Unity 2>&1 | ForEach-Object { $l = "$_"; if ($l -match 'SfAvatar: (built|verify)|avatar ->|FAILED|error CS|copied') { Write-Host "  $l" } }
    $code = $LASTEXITCODE; $ErrorActionPreference = $eap
    if ($code -ne 0) { throw "the bundle build failed (log: $env:TEMP\sfavatar_build.log)" }
    $out = Join-Path $store "avatars\$name"
    if ($install) {
        if (-not $cfg.Game -or -not (Test-Path $cfg.Game)) { throw 'the game folder is not set: cannot install' }
        $dst = Join-Path $cfg.Game "BepInEx\config\SfMorphs\avatars\$name"
        New-Item -ItemType Directory -Force $dst | Out-Null
        Copy-Item -Force (Join-Path $out '*') $dst
        Write-Host "installed into the game: $dst (Characters tab > Avatars)"
    } else { Write-Host "avatar ready: $out (copy the folder into <game>\BepInEx\config\SfMorphs\avatars, or tick 'Install into the game')" }
}

function Invoke-Vam($cfg, $looks, [bool]$keep) {
    $vam = $cfg.Vam
    if (-not $vam -or -not (Test-Path (Join-Path $vam 'VaM.exe'))) { throw 'VaM not found: set the VaM folder (the one with VaM.exe)' }
    if (-not $looks) { throw 'no VaM looks (appearance presets or scene Persons) in the source' }
    Write-Host "VaM looks: $(($looks | ForEach-Object { $_.Label }) -join ', ')"
    # our two pieces in VaM: the export plugin (a VaM script) and the BepInEx helper that loads the export scene and closes VaM
    $sm = Join-Path $vam 'Custom\Scripts\SfMod'; New-Item -ItemType Directory -Force $sm | Out-Null
    Copy-Item -Force (Join-Path $here '..\vam\SfExport.cs') $sm
    if (-not (Test-Path (Join-Path $sm 'morphs.cfg'))) { Copy-Item (Join-Path $here '..\vam\morphs.cfg') $sm }
    $bep = Join-Path $vam 'BepInEx\plugins'
    $bridge = Test-Path $bep
    if ($bridge) { Copy-Item -Force (Join-Path $here '..\vam\SfVamBridge\SfVamBridge.dll') $bep }
    $auto = Join-Path $vam 'Saves\PluginData\SfExport\auto'
    New-Item -ItemType Directory -Force $auto | Out-Null
    foreach ($f in 'done.txt', 'status.txt', 'bridge.txt', 'quit') { Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $auto $f) }
    # the scene: VaM's default scene with plugins removed and SfExport on its first Person (a bare Person when there is none)
    $def = Join-Path $vam 'Saves\scene\default.json'
    $scene = $null
    if (Test-Path $def) { try { $scene = Get-Content $def -Raw | ConvertFrom-Json } catch {} }
    if (-not $scene -or -not ($scene.atoms | Where-Object { $_.type -eq 'Person' })) {
        $scene = [pscustomobject]@{ atoms = @([pscustomobject]@{ id = 'CoreControl'; on = 'true'; type = 'CoreControl'; storables = @() }, [pscustomobject]@{ id = 'Person'; on = 'true'; type = 'Person'; position = @{ x = '0'; y = '0'; z = '0' }; storables = @() }) }
    }
    $first = $true
    foreach ($a in $scene.atoms) {
        $st = @($a.storables | Where-Object { $_.id -ne 'PluginManager' -and $_.id -notlike 'plugin#*' })
        if ($a.type -eq 'Person' -and $first) { $st += [pscustomobject]@{ id = 'PluginManager'; plugins = [pscustomobject]@{ 'plugin#0' = 'Custom/Scripts/SfMod/SfExport.cs' } }; $first = $false }
        $a.storables = $st
    }
    [IO.File]::WriteAllText((Join-Path $auto 'scene.json'), ($scene | ConvertTo-Json -Depth 100), (New-Object Text.UTF8Encoding $false))
    $req = ($looks | ForEach-Object { "$($_.Path)`t$($_.Label)" }) -join "`n"
    [IO.File]::WriteAllText((Join-Path $auto 'request.txt'), "# SF Avatar Import: appearance preset <tab> label`n$req`n", (New-Object Text.UTF8Encoding $false))
    if (-not $keep) { Set-Content (Join-Path $auto 'quit') '' }
    $proc = $null
    if ($bridge -and -not (Get-Process VaM -ErrorAction SilentlyContinue)) {
        Write-Host 'starting VaM in desktop mode: it loads each look, exports it and closes (do not close it meanwhile) ...'
        $proc = Start-Process -FilePath (Join-Path $vam 'VaM.exe') -ArgumentList '-vrmode', 'None' -WorkingDirectory $vam -PassThru
    } else {
        Write-Host '*** VaM is running already, or has no BepInEx: in VaM, open the scene Saves\PluginData\SfExport\auto\scene.json (File > Open);'
        Write-Host '*** the export then runs by itself. Waiting for it ...'
    }
    $t0 = Get-Date; $last = ''
    while (-not (Test-Path (Join-Path $auto 'done.txt'))) {
        Start-Sleep -Seconds 2
        $s = if (Test-Path (Join-Path $auto 'status.txt')) { (Get-Content (Join-Path $auto 'status.txt') -Raw).Trim() } else { '' }
        if ($s -and $s -ne $last) { Write-Host "  VaM: $s"; $last = $s }
        if ($proc -and $proc.HasExited) { throw 'VaM closed before the export finished (its log: ' + (Join-Path $vam 'BepInEx\LogOutput.log') + ')' }
        if (((Get-Date) - $t0).TotalMinutes -gt 60) { throw 'no export after an hour: look at VaM' }
    }
    Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $auto 'request.txt')   # VaM starts normally again
    $res = @()
    foreach ($l in Get-Content (Join-Path $auto 'done.txt')) {
        $t = $l -split "`t"
        if ($t.Count -lt 3) { continue }
        if ($t[0] -eq 'ok') { $res += [pscustomobject]@{ Label = $t[1]; Dir = (Join-Path $vam ($t[2] -replace '/', '\')) } }
        else { Write-Host "  WARNING: look $($t[1]) failed in VaM: $($t[2])" }
    }
    if ($proc -and -not $keep -and -not $proc.HasExited) {
        # the bridge asks VaM to quit; VaM (or one of its plugins) can hang on the way out: the export is on disk, so the instance
        # this import started is ended after a minute
        $proc.WaitForExit(60000) | Out-Null
        if (-not $proc.HasExited) { Write-Host '  VaM did not finish closing: ending the instance this import started'; Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    }
    return $res
}

function Invoke-Daz($cfg, [string]$duf) {
    if (-not $cfg.Daz -or -not (Test-Path $cfg.Daz)) { throw 'DAZ Studio not found: install it, or set DAZStudio.exe in the window' }
    if (Get-Process DAZStudio -ErrorAction SilentlyContinue) { throw 'DAZ Studio is running: close it (save your scene first) and import again' }
    $leaf = Safe ([IO.Path]::GetFileNameWithoutExtension($duf))
    $fbx = Join-Path $store "daz\$leaf\$leaf.fbx"
    New-Item -ItemType Directory -Force (Split-Path $fbx) | Out-Null
    Remove-Item "$fbx.status.txt" -ErrorAction SilentlyContinue
    Write-Host "DAZ Studio exports $([IO.Path]::GetFileName($duf)) (it opens, exports and closes) ..."
    $dsa = (Resolve-Path (Join-Path $here '..\sources\daz_export.dsa')).Path
    # "morphs": the figure's joint correctives come along as blend shapes (Avatar.cs drives them by DAZ's own formulas)
    $proc = Start-Process -FilePath $cfg.Daz -ArgumentList @('-scriptArg', "`"$duf`"", '-scriptArg', "`"$fbx`"", '-scriptArg', 'morphs', "`"$dsa`"") -PassThru
    $t0 = Get-Date
    while (-not $proc.HasExited -and -not (Test-Path "$fbx.status.txt") -and ((Get-Date) - $t0).TotalMinutes -lt 10) { Start-Sleep -Seconds 2 }
    if (-not (Test-Path "$fbx.status.txt")) { throw 'DAZ Studio did not finish in 10 minutes (a dialog is probably open in its window: missing files, login)' }
    if (-not $proc.HasExited) { $proc.WaitForExit(90000) | Out-Null }
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    $st = (Get-Content "$fbx.status.txt" -Raw).Trim()
    Write-Host "  DAZ: $st"
    if (-not $st.StartsWith('OK') -or -not (Test-Path $fbx)) { throw "the DAZ export failed: $st" }
    # the correctives' drivers (DAZ's formulas in the figure's .dsf files) -> <fbx>.correctives.txt, which build.ps1 ships with the bundle
    if (Test-Path "$fbx.morphs.txt") {
        $bpy = Get-ChildItem (Split-Path $cfg.Blender) -Recurse -Filter python.exe -Depth 3 -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\python\\bin\\' } | Select-Object -First 1
        if ($bpy) {
            & $bpy.FullName (Join-Path $here '..\sources\daz_correctives.py') "$fbx.morphs.txt" "$fbx.correctives.txt" 2>&1 | ForEach-Object { Write-Host "  $_" }
        } else { Write-Host "  no Python next to Blender: joint correctives exported without drivers (they stay off)" }
    }
    return $fbx
}

function Invoke-Import([string]$src, [string]$name, [string]$look, [bool]$install, [bool]$keep) {
    $cfg = Resolve-Tools (Get-Cfg)
    Save-Cfg $cfg
    if (-not (Test-Path -LiteralPath $src)) { throw "no such file or folder: $src" }
    $base = if ($name) { $name } else { [IO.Path]::GetFileNameWithoutExtension($src.TrimEnd('\')) }
    $work = Join-Path $store ("import\" + (Safe $base))
    New-Item -ItemType Directory -Force $work | Out-Null
    Write-Host "source: $src"
    $k = Get-SourceKind $src $work $cfg
    Write-Host "kind: $($k.Kind)"
    $made = @()
    switch ($k.Kind) {
        'vam' {
            $looks = @($k.Looks)
            if ($look) { $looks = @($looks | Where-Object { $_.Label -like "*$look*" }) }
            foreach ($r in Invoke-Vam $cfg $looks $keep) {
                $n = if ($name -and $looks.Count -eq 1) { $name } else { $r.Label }
                $made += @{ Export = $r.Dir; Name = $n }
            }
        }
        'sfexport' { $made += @{ Export = $k.Dir; Name = $(if ($name) { $name } else { $base }) } }
        'daz' {
            $dufs = @($k.Duf)
            if ($look) { $dufs = @($dufs | Where-Object { [IO.Path]::GetFileNameWithoutExtension($_) -like "*$look*" }) }
            if (-not $dufs) { throw "no DAZ character preset matches '$look'" }
            if ($dufs.Count -gt 1) { Write-Host "character presets: $(($dufs | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) }) -join ', '); taking the first (-Look picks another)" }
            $fbx = Invoke-Daz $cfg $dufs[0]
            $made += @{ Fbx = $fbx; Name = $(if ($name) { $name } else { [IO.Path]::GetFileNameWithoutExtension($dufs[0]) }) }
        }
        'fbx' { $made += @{ Fbx = $k.Fbx; Name = $(if ($name) { $name } else { [IO.Path]::GetFileNameWithoutExtension($k.Fbx) }) } }
    }
    if (-not $made) { throw 'nothing to build' }
    foreach ($m in $made) {
        $n = Safe $m.Name
        Write-Host "---- $n"
        $fbx = $m.Fbx
        if ($m.Export) {
            if (-not (Test-Path (Join-Path $m.Export 'weights.txt'))) { throw "$($m.Export) has no skin weights (weights.txt): it was exported by an older SfExport. Import the VaM look itself (.var / preset), or export it again in VaM" }
            $fd = Join-Path $store "import\$n\fbx"
            Write-Host 'making the FBX (Blender) ...'
            Invoke-Blender $cfg @('-P', (Join-Path $here 'vam_avatar.py'), '--', '--vam', $m.Export, '--out', $fd, '--name', $n)
            $fbx = Join-Path $fd "$n.fbx"
        }
        Invoke-Build $cfg $fbx $n $install
    }
    Write-Host "DONE: $(($made | ForEach-Object { Safe $_.Name }) -join ', ')"
}

if ($Source -or $NoGui) {
    if (-not $Source) { throw '-Source is needed without the window' }
    try { Invoke-Import $Source $Name $Look ([bool]$Install) ([bool]$KeepVam); exit 0 }
    catch { Write-Host "FAILED: $($_.Exception.Message)"; exit 1 }
}

# ---- the window -------------------------------------------------------------------------------------------------------------
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$cfg = Resolve-Tools (Get-Cfg)
$f = New-Object Windows.Forms.Form
$f.Text = 'SF Avatar Import'; $f.Size = New-Object Drawing.Size(860, 700); $f.StartPosition = 'CenterScreen'
$f.Font = New-Object Drawing.Font('Segoe UI', 9)
$y = 12
function Add-Label($t, $x, $yy, $w = 120) { $l = New-Object Windows.Forms.Label; $l.Text = $t; $l.Location = New-Object Drawing.Point($x, ($yy + 3)); $l.Size = New-Object Drawing.Size($w, 20); $f.Controls.Add($l); $l }
function Add-Box($x, $yy, $w) { $b = New-Object Windows.Forms.TextBox; $b.Location = New-Object Drawing.Point($x, $yy); $b.Size = New-Object Drawing.Size($w, 23); $f.Controls.Add($b); $b }
function Add-Button($t, $x, $yy, $w, $on) { $b = New-Object Windows.Forms.Button; $b.Text = $t; $b.Location = New-Object Drawing.Point($x, ($yy - 1)); $b.Size = New-Object Drawing.Size($w, 25); $b.Add_Click($on); $f.Controls.Add($b); $b }

Add-Label 'Character' 12 $y | Out-Null
$src = Add-Box 140 $y 470
Add-Button 'Zip / file...' 618 $y 105 {
    $d = New-Object Windows.Forms.OpenFileDialog
    $d.Filter = 'Characters (*.zip;*.var;*.vap;*.json;*.duf;*.fbx)|*.zip;*.var;*.vap;*.json;*.duf;*.fbx|All files|*.*'
    if ($d.ShowDialog() -eq 'OK') { $src.Text = $d.FileName; if (-not $nameBox.Text) { $nameBox.Text = [IO.Path]::GetFileNameWithoutExtension($d.FileName) -replace '^Preset_', '' } }
} | Out-Null
Add-Button 'Folder...' 728 $y 100 {
    $d = New-Object Windows.Forms.FolderBrowserDialog
    if ($d.ShowDialog() -eq 'OK') { $src.Text = $d.SelectedPath; if (-not $nameBox.Text) { $nameBox.Text = Split-Path -Leaf $d.SelectedPath } }
} | Out-Null
$y += 32
Add-Label 'Avatar name' 12 $y | Out-Null
$nameBox = Add-Box 140 $y 250
Add-Label 'Only looks named' 410 $y 110 | Out-Null
$lookBox = Add-Box 520 $y 200
$y += 34
$tools = @(
    @{ Key = 'Unity'; Label = 'Unity 2022.3.9f1'; Filter = 'Unity.exe|Unity.exe'; Need = 'always (install the game''s version through Unity Hub)' },
    @{ Key = 'Blender'; Label = 'Blender'; Filter = 'blender.exe|blender.exe'; Need = 'always (blender.org or Steam)' },
    @{ Key = 'Vam'; Label = 'VaM folder'; Folder = $true; Need = 'VaM looks' },
    @{ Key = 'Daz'; Label = 'DAZ Studio'; Filter = 'DAZStudio.exe|DAZStudio.exe'; Need = 'DAZ characters' },
    @{ Key = 'Game'; Label = 'Game folder'; Folder = $true; Need = 'installing into the game' }
)
$boxes = @{}; $marks = @{}
$grp = New-Object Windows.Forms.GroupBox; $grp.Text = 'Software (found automatically where possible; remembered)'; $grp.Location = New-Object Drawing.Point(8, $y); $grp.Size = New-Object Drawing.Size(828, 180); $f.Controls.Add($grp)
$gy = 22
foreach ($t in $tools) {
    $l = New-Object Windows.Forms.Label; $l.Text = $t.Label; $l.Location = New-Object Drawing.Point(8, ($gy + 3)); $l.Size = New-Object Drawing.Size(120, 20); $grp.Controls.Add($l)
    $b = New-Object Windows.Forms.TextBox; $b.Location = New-Object Drawing.Point(130, $gy); $b.Size = New-Object Drawing.Size(470, 23); $b.Text = [string]$cfg[$t.Key]; $grp.Controls.Add($b)
    $m = New-Object Windows.Forms.Label; $m.Location = New-Object Drawing.Point(690, ($gy + 3)); $m.Size = New-Object Drawing.Size(130, 20); $grp.Controls.Add($m)
    $btn = New-Object Windows.Forms.Button; $btn.Text = 'Browse...'; $btn.Location = New-Object Drawing.Point(606, ($gy - 1)); $btn.Size = New-Object Drawing.Size(78, 25); $grp.Controls.Add($btn)
    $tt = $t; $bb = $b
    $btn.Add_Click({
        if ($tt.Folder) { $d = New-Object Windows.Forms.FolderBrowserDialog; if ($d.ShowDialog() -eq 'OK') { $bb.Text = $d.SelectedPath } }
        else { $d = New-Object Windows.Forms.OpenFileDialog; $d.Filter = $tt.Filter; if ($d.ShowDialog() -eq 'OK') { $bb.Text = $d.FileName } }
    }.GetNewClosure())
    $boxes[$t.Key] = $b; $marks[$t.Key] = $m
    $gy += 30
}
function Update-Marks {
    foreach ($t in $tools) {
        $v = $boxes[$t.Key].Text.Trim()
        $ok = $v -and (Test-Path -LiteralPath $v)
        if ($ok -and $t.Key -eq 'Vam') { $ok = Test-Path (Join-Path $v 'VaM.exe') }
        $marks[$t.Key].Text = if ($ok) { 'found' } else { 'needed for ' + ($t.Need -split ' \(')[0] }
        $marks[$t.Key].ForeColor = if ($ok) { [Drawing.Color]::DarkGreen } else { [Drawing.Color]::DarkOrange }
    }
}
foreach ($b in $boxes.Values) { $b.Add_TextChanged({ Update-Marks }) }
Update-Marks
$y += 188
$inst = New-Object Windows.Forms.CheckBox; $inst.Text = 'Install into the game when done'; $inst.Checked = $true; $inst.Location = New-Object Drawing.Point(12, $y); $inst.Size = New-Object Drawing.Size(260, 22); $f.Controls.Add($inst)
$keepV = New-Object Windows.Forms.CheckBox; $keepV.Text = 'Leave VaM open after its export'; $keepV.Location = New-Object Drawing.Point(280, $y); $keepV.Size = New-Object Drawing.Size(260, 22); $f.Controls.Add($keepV)
$go = Add-Button 'Import' 728 $y 100 { }
$y += 32
$log = New-Object Windows.Forms.TextBox; $log.Multiline = $true; $log.ScrollBars = 'Vertical'; $log.ReadOnly = $true; $log.Font = New-Object Drawing.Font('Consolas', 9)
$log.Location = New-Object Drawing.Point(12, $y); $log.Size = New-Object Drawing.Size(816, (640 - $y)); $log.Anchor = 'Top,Bottom,Left,Right'; $f.Controls.Add($log)
$script:proc = $null
$queue = New-Object System.Collections.Concurrent.ConcurrentQueue[string]
$timer = New-Object Windows.Forms.Timer; $timer.Interval = 300
$timer.Add_Tick({
    $s = $null
    while ($queue.TryDequeue([ref]$s)) { $log.AppendText($s + "`r`n") }
    if ($script:proc -and $script:proc.HasExited) {
        $code = $script:proc.ExitCode; $script:proc = $null
        $log.AppendText($(if ($code -eq 0) { "`r`nFinished." } else { "`r`nStopped with an error (above)." }) + "`r`n")
        $go.Enabled = $true
    }
})
$timer.Start()
$go.Add_Click({
    if (-not $src.Text.Trim()) { [Windows.Forms.MessageBox]::Show('Pick a character first (a zip, .var, preset, .duf, .fbx or a folder).') | Out-Null; return }
    $c = Get-Cfg
    foreach ($t in $tools) { $v = $boxes[$t.Key].Text.Trim(); if ($v) { $c[$t.Key] = $v } }
    Save-Cfg $c
    $log.Clear(); $go.Enabled = $false
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = 'powershell.exe'
    $a = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -NoGui -Source `"$($src.Text.Trim())`""
    if ($nameBox.Text.Trim()) { $a += " -Name `"$($nameBox.Text.Trim())`"" }
    if ($lookBox.Text.Trim()) { $a += " -Look `"$($lookBox.Text.Trim())`"" }
    if ($inst.Checked) { $a += ' -Install' }
    if ($keepV.Checked) { $a += ' -KeepVam' }
    $psi.Arguments = $a
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
    $p = New-Object Diagnostics.Process; $p.StartInfo = $psi
    $q = $queue
    $h = [Diagnostics.DataReceivedEventHandler] { param($s, $e) if ($e.Data -ne $null) { $q.Enqueue($e.Data) } }.GetNewClosure()
    $p.add_OutputDataReceived($h); $p.add_ErrorDataReceived($h)
    $p.Start() | Out-Null; $p.BeginOutputReadLine(); $p.BeginErrorReadLine()
    $script:proc = $p
})
[void]$f.ShowDialog()
