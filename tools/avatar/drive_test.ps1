# Runs the plugin's src/SfMorph/Avatar.cs in the Unity editor on her export and a pose recording (AvatarDriveTest): the avatar fitted
# onto her skeleton, then per frame how far her body skin is from the avatar's, the soles against hers, the skin's stretch, giving
# back; OBJs of the avatar and her for a look.
#   powershell -File tools\avatar\drive_test.ps1 -Export <her SfMorph export dir> -Pose <recording.txt> -Avatar <Name>
#              [-Store %LOCALAPPDATA%\SfMorph] [-Out <dir>] [-Frames 4]
param(
    [Parameter(Mandatory = $true)][string]$Export,
    [Parameter(Mandatory = $true)][string]$Pose,
    [Parameter(Mandatory = $true)][string]$Avatar,
    [string]$Store = (Join-Path $env:LOCALAPPDATA "SfMorph"),
    [string]$Out = (Join-Path $env:TEMP "sfavatar_drive"),
    [int]$Frames = 4,
    [string]$Unity = "C:\Program Files\Unity\Hub\Editor\2022.3.9f1\Editor\Unity.exe"
)
$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "SfAvatarBundle"
$copied = Join-Path $proj "Assets\Editor\DriveTest\Copied"
New-Item -ItemType Directory -Force $copied | Out-Null
Copy-Item -Force (Join-Path $PSScriptRoot "..\..\src\SfMorph\Avatar.cs") $copied
Copy-Item -Force (Join-Path $PSScriptRoot "..\..\src\SfMorph\MeshData.cs") $copied
$log = Join-Path $env:TEMP "sfavatar_drive.log"
& $Unity -batchmode -quit -nographics -projectPath $proj -executeMethod AvatarDriveTest.Run -export $Export -pose $Pose -store $Store `
    -avatar $Avatar -out $Out -frames "$Frames" -logFile $log | Out-Null
$code = $LASTEXITCODE
Select-String -Path $log -Pattern "SfAvatarTest|error CS" | ForEach-Object { $_.Line }
if ($code -ne 0 -or (Select-String -Path $log -Pattern "SfAvatarTest FAILED|error CS" -Quiet)) { throw "drive test failed (exit $code, log: $log)" }
"results: $Out"
