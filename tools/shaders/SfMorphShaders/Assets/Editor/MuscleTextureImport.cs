using UnityEditor;

// Import settings of the authored muscle maps (tools/muscles/author.py -> Assets/SfMorph/Muscles, 0.10.0):
//   *_detail.png  linear data (relief slope, groove depth, coverage): no sRGB, mips, BC7, clamped.
//   *_region.png  muscle id pairs + share, point-fetched in the blend (ids can't be filtered or compressed): uncompressed RGBA32, no
//                 mips, point filter, linear. Not read on the CPU (unlike the wrinkle region maps), so not readable.
//   *_height.png  (0.11.12) the relief's height in 16 bits over R / G for the geometry bake: the same settings as the region map.
public class MuscleTextureImport : AssetPostprocessor
{
    void OnPreprocessTexture()
    {
        if (!assetPath.Contains("/SfMorph/Muscles/")) return;
        var ti = (TextureImporter)assetImporter;
        ti.textureType = TextureImporterType.Default;
        ti.sRGBTexture = false;
        ti.alphaSource = TextureImporterAlphaSource.FromInput;
        ti.alphaIsTransparency = false;
        ti.npotScale = TextureImporterNPOTScale.None;
        ti.wrapMode = UnityEngine.TextureWrapMode.Clamp;
        ti.maxTextureSize = 4096;
        ti.isReadable = false;
        if (assetPath.EndsWith("_region.png") || assetPath.EndsWith("_height.png"))   // 0.11.12: the 16-bit height (two bytes) must stay exact
        {
            ti.mipmapEnabled = false;
            ti.filterMode = UnityEngine.FilterMode.Point;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            var ps = ti.GetDefaultPlatformTextureSettings();
            ps.format = TextureImporterFormat.RGBA32;
            ps.textureCompression = TextureImporterCompression.Uncompressed;
            ti.SetPlatformTextureSettings(ps);
            var st = ti.GetPlatformTextureSettings("Standalone");
            st.overridden = true;
            st.format = TextureImporterFormat.RGBA32;
            st.maxTextureSize = 4096;
            ti.SetPlatformTextureSettings(st);
        }
        else
        {
            ti.mipmapEnabled = true;
            ti.filterMode = UnityEngine.FilterMode.Trilinear;
            ti.anisoLevel = 4;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            var ps = ti.GetPlatformTextureSettings("Standalone");
            ps.overridden = true;
            ps.format = TextureImporterFormat.BC7;
            ps.maxTextureSize = 4096;
            ti.SetPlatformTextureSettings(ps);
        }
    }
}
