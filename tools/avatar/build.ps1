# Builds an avatar bundle for SF Body Morphs' avatar replacement mode (docs/SOURCES.md) with Unity 2022.3.9f1, the game's version.
#   powershell -File tools\avatar\build.ps1 -Fbx "<character>.fbx" [-Name <Name>] [-Out <folder>] [-MaxSize 4096] [-MapSize 2048]
# Input: an FBX with its skeleton (DAZ Studio via tools/sources/daz_export.dsa writes <fbx>, <fbx>.materials.txt and
# <name>.images). The character is copied into SfAvatarBundle\Assets\Avatars\<Name> (git-ignored: never commit it), built,
# then loaded back and checked (AvatarBundle.Verify). Output: <Out>\<name> (the bundle) and <Out>\avatar.txt.
# Default Out: %LOCALAPPDATA%\SfMorph\avatars\<Name>. Nothing is copied into the game.
param(
    [Parameter(Mandatory = $true)][string]$Fbx,
    [string]$Name,
    [string]$Out,
    [int]$MaxSize = 4096,
    [int]$MapSize = 2048,
    [string]$Unity = "C:\Program Files\Unity\Hub\Editor\2022.3.9f1\Editor\Unity.exe"
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path $Unity)) { throw "Unity 2022.3.9f1 not found at $Unity (pass -Unity <path to Unity.exe>)" }
$Fbx = (Resolve-Path $Fbx).Path
if (-not $Name) { $Name = [IO.Path]::GetFileNameWithoutExtension($Fbx) }
if (-not $Out) { $Out = Join-Path $env:LOCALAPPDATA "SfMorph\avatars\$Name" }
$proj = Join-Path $PSScriptRoot "SfAvatarBundle"
$dst = Join-Path $proj "Assets\Avatars\$Name"

# 1. the character into the project
if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
if (Test-Path "$dst.meta") { Remove-Item -Force "$dst.meta" }
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item $Fbx $dst
$seen = @{}
function Add-Texture([string]$p) {
    if (-not (Test-Path -LiteralPath $p)) { Write-Warning "texture not found: $p"; return }
    $f = Split-Path -Leaf $p
    if ($seen.ContainsKey($f)) {   # DAZ's <name>.images holds copies of the library files: warn only when they differ
        if ((Get-Item -LiteralPath $p).Length -ne (Get-Item -LiteralPath $seen[$f]).Length) { Write-Warning "two different textures named $f, kept $($seen[$f])" }
        return
    }
    $seen[$f] = $p
    Copy-Item -LiteralPath $p (Join-Path $dst $f)
}
$mat = "$Fbx.materials.txt"
if (Test-Path -LiteralPath $mat) {
    Copy-Item -LiteralPath $mat (Join-Path $dst "materials.txt")
    foreach ($l in Get-Content -LiteralPath $mat) {
        if ($l.StartsWith("#") -or -not $l.Trim()) { continue }
        $t = $l -split "`t"
        if ($t.Count -ge 3) { Add-Texture $t[2].Trim() }
    }
}
$base = Join-Path (Split-Path $Fbx) ([IO.Path]::GetFileNameWithoutExtension($Fbx))
foreach ($d in @("$base.images", "$base.fbm", (Join-Path (Split-Path $Fbx) "textures"))) {
    if (Test-Path -LiteralPath $d) {
        Get-ChildItem -LiteralPath $d -File | Where-Object { $_.Extension -match '^\.(png|jpe?g|tga|tiff?|bmp)$' } | ForEach-Object { Add-Texture $_.FullName }
    }
}
"copied $Name`: FBX + $($seen.Count) textures -> $dst"

# 2. build, 3. verify
$build = Join-Path $proj "Build\$Name"
if (Test-Path $build) { Remove-Item -Recurse -Force $build }
function Run-Unity([string]$method, [string[]]$more, [string]$log) {
    & $Unity -batchmode -quit -nographics -projectPath $proj -executeMethod $method @more -logFile $log | Out-Null
    $code = $LASTEXITCODE
    Select-String -Path $log -Pattern "SfAvatar|error CS|Exception" | Select-Object -Last 40 | ForEach-Object { $_.Line }
    if ($code -ne 0 -or (Select-String -Path $log -Pattern "SfAvatar FAILED" -Quiet)) { throw "$method failed (exit $code, log: $log)" }
}
Run-Unity "AvatarBundle.Build" @("-name", $Name, "-out", $build, "-maxsize", "$MaxSize", "-mapsize", "$MapSize") (Join-Path $env:TEMP "sfavatar_build.log")
$bundleName = ($Name.ToLowerInvariant() -replace '[^a-z0-9]+', '_').Trim('_')
$bundle = Join-Path $build $bundleName
if (-not (Test-Path $bundle)) { throw "no bundle at $bundle" }
Run-Unity "AvatarBundle.Verify" @("-bundle", $bundle) (Join-Path $env:TEMP "sfavatar_verify.log")

New-Item -ItemType Directory -Force $Out | Out-Null
Copy-Item -Force $bundle $Out
Copy-Item -Force (Join-Path $build "avatar.txt") $Out
"avatar -> " + (Join-Path $Out $bundleName) + " (" + [math]::Round((Get-Item $bundle).Length / 1MB, 1) + " MB)"
