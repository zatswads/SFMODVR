using UnityEditor;

// Import settings of the authored wrinkle maps (tools/wrinkles/author.py -> Assets/SfMorph/Wrinkles):
//   *_detail.png  linear data (slope, crease depth, region fade): no sRGB, mips, BC7, clamped.
//   *_region.png  region ids read on the CPU (Texture2D.GetPixels32) and point-fetched in the blend: readable, uncompressed RGBA32,
//                 no mips, point filter, linear.
public class WrinkleTextureImport : AssetPostprocessor
{
    void OnPreprocessTexture()
    {
        if (!assetPath.Contains("/SfMorph/Wrinkles/")) return;
        var ti = (TextureImporter)assetImporter;
        ti.textureType = TextureImporterType.Default;
        ti.sRGBTexture = false;
        ti.alphaSource = TextureImporterAlphaSource.FromInput;
        ti.alphaIsTransparency = false;
        ti.npotScale = TextureImporterNPOTScale.None;
        ti.wrapMode = UnityEngine.TextureWrapMode.Clamp;
        ti.maxTextureSize = 4096;
        if (assetPath.EndsWith("_region.png"))
        {
            ti.isReadable = true;
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
            ti.isReadable = false;
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
