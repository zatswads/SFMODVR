using System.IO;
using UnityEditor;
using UnityEngine;

// Batch build of SfMorph's shader bundle for Sex Formula (Unity 2022.3.9f1, Windows x64, D3D11):
//   Unity.exe -batchmode -quit -nographics -projectPath <this project> -executeMethod BuildBundles.Build -out <folder>
// Everything under Assets/SfMorph goes into one bundle, "sfmorph_shaders".
public static class BuildBundles
{
    public static void Build()
    {
        string outDir = "Build";
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-out") outDir = args[i + 1];
        Directory.CreateDirectory(outDir);
        var assets = AssetDatabase.FindAssets("", new[] { "Assets/SfMorph" });
        var paths = new System.Collections.Generic.List<string>();
        foreach (var g in assets)
        {
            var p = AssetDatabase.GUIDToAssetPath(g);
            if (!AssetDatabase.IsValidFolder(p)) paths.Add(p);
        }
        var build = new AssetBundleBuild { assetBundleName = "sfmorph_shaders", assetNames = paths.ToArray() };
        var manifest = BuildPipeline.BuildAssetBundles(outDir, new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, BuildTarget.StandaloneWindows64);
        if (manifest == null) { Debug.LogError("SfMorph bundle build failed"); EditorApplication.Exit(1); return; }
        Debug.Log("SfMorph bundle: " + paths.Count + " assets -> " + Path.GetFullPath(Path.Combine(outDir, "sfmorph_shaders")));
    }
}
