#!/bin/sh
# Builds tools/vam/SfVamBridge/SfVamBridge.dll (the VaM-side helper of tools/avatar/import.ps1) and compile-checks
# tools/vam/SfExport.cs, both against VaM's own assemblies, with the Mono compiler that comes with Unity.
#   VAMREF=<folder with VaM_Data\Managed\*.dll and BepInEx\core\BepInEx.dll> sh tools/vam/build_bridge.sh
# Unity 2022.3.9f1's MonoBleedingEdge by default (UNITYMONO). The DLL is committed (import.ps1 installs it); it holds no VaM code.
set -e
R="${VAMREF:?set VAMREF to a folder with VaM's Managed DLLs and BepInEx.dll}"
U="${UNITYMONO:-/c/Program Files/Unity/Hub/Editor/2022.3.9f1/Editor/Data/MonoBleedingEdge}"
here=$(dirname "$0")
refs="-r:$R/mscorlib.dll -r:$R/System.dll -r:$R/System.Core.dll -r:$R/Assembly-CSharp.dll -r:$R/UnityEngine.dll -r:$R/UnityEngine.CoreModule.dll"
"$U/bin/mono.exe" "$U/lib/mono/4.5/mcs.exe" -nostdlib -noconfig -target:library -out:"$here/SfVamBridge/SfVamBridge.dll" \
  $refs -r:"$R/BepInEx.dll" "$here/SfVamBridge/SfVamBridge.cs"
"$U/bin/mono.exe" "$U/lib/mono/4.5/mcs.exe" -nostdlib -noconfig -target:library -out:"${TMP:-/tmp}/SfExport_check.dll" \
  $refs -r:"$R/UnityEngine.UI.dll" -r:"$R/UnityEngine.ImageConversionModule.dll" "$here/SfExport.cs"
echo "built $here/SfVamBridge/SfVamBridge.dll; SfExport.cs compiles"
