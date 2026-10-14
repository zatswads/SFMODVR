#!/bin/sh
# Builds dist/SfMorph.dll with Mono's mcs against the game's assemblies.
# REFS must hold, from the game's Sex Formula_Data\Managed: UnityEngine.dll, UnityEngine.CoreModule.dll,
# UnityEngine.IMGUIModule.dll, UnityEngine.TextRenderingModule.dll, UnityEngine.InputLegacyModule.dll,
# UnityEngine.ImageConversionModule.dll;
# and BepInEx\core's BepInEx.dll. (Assembly-CSharp is read by reflection, not referenced.)
set -e
R="${REFS:?set REFS to a folder with the reference DLLs}"
mkdir -p dist
mcs -target:library -langversion:7.2 -out:dist/SfMorph.dll \
  -r:"$R/UnityEngine.dll" -r:"$R/UnityEngine.CoreModule.dll" -r:"$R/UnityEngine.IMGUIModule.dll" \
  -r:"$R/UnityEngine.TextRenderingModule.dll" -r:"$R/UnityEngine.InputLegacyModule.dll" -r:"$R/UnityEngine.ImageConversionModule.dll" -r:"$R/BepInEx.dll" \
  -r:/usr/lib/mono/4.5/Facades/netstandard.dll src/SfMorph/*.cs
