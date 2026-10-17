// SF VaM bridge: a BepInEx 5 plugin for Virt-A-Mate that lets tools/avatar/import.ps1 export VaM looks without anyone clicking
// in VaM. It does nothing unless <VaM>\Saves\PluginData\SfExport\auto\request.txt exists and done.txt does not: then, once VaM
// has started, it loads auto\scene.json (a Person with Custom/Scripts/SfMod/SfExport.cs, written by import.ps1); SfExport's
// auto mode loads each requested appearance preset and exports it (weights included), then writes done.txt. With auto\quit
// present the bridge closes VaM after that. Progress: auto\status.txt (SfExport) and auto\bridge.txt (this plugin).
// Built by tools/vam/build_bridge.sh against VaM's own assemblies (CLR 2 / .NET 3.5, like VaM).
using System;
using System.Collections;
using System.IO;
using BepInEx;
using UnityEngine;

[BepInPlugin("sfmorph.vambridge", "SF VaM bridge", "1.0.0")]
public class SfVamBridge : BaseUnityPlugin
{
    string _dir;

    void Start()
    {
        _dir = Path.Combine(Path.Combine(Path.Combine(Paths.GameRootPath, "Saves"), "PluginData"), Path.Combine("SfExport", "auto"));
        if (!File.Exists(Path.Combine(_dir, "request.txt")) || File.Exists(Path.Combine(_dir, "done.txt"))) return;
        if (!File.Exists(Path.Combine(_dir, "scene.json"))) { Note("request.txt without scene.json: nothing to load"); return; }
        StartCoroutine(Run());
    }

    void Note(string s)
    {
        Logger.LogInfo(s);
        try { File.AppendAllText(Path.Combine(_dir, "bridge.txt"), DateTime.Now.ToString("HH:mm:ss") + "\t" + s + "\n"); } catch { }
    }

    IEnumerator Run()
    {
        float t0 = Time.realtimeSinceStartup;
        Note("waiting for VaM to start");
        while (SuperController.singleton == null && Time.realtimeSinceStartup - t0 < 600f) yield return new WaitForSeconds(0.5f);
        if (SuperController.singleton == null) { Note("VaM's SuperController never came up"); yield break; }
        // VaM loads its startup scene first: let it finish, then replace it
        yield return new WaitForSeconds(5f);
        while (SuperController.singleton.isLoading) yield return new WaitForSeconds(0.5f);
        Note("loading the export scene");
        try { SuperController.singleton.Load("Saves/PluginData/SfExport/auto/scene.json"); }
        catch (Exception e) { Note("loading the scene failed: " + e.Message); yield break; }
        float t1 = Time.realtimeSinceStartup;
        string done = Path.Combine(_dir, "done.txt");
        while (!File.Exists(done) && Time.realtimeSinceStartup - t1 < 3600f) yield return new WaitForSeconds(1f);
        Note(File.Exists(done) ? "the export is done" : "gave up after an hour without done.txt");
        if (File.Exists(Path.Combine(_dir, "quit")))
        {
            yield return new WaitForSeconds(2f);
            Note("closing VaM");
            try { SuperController.singleton.Quit(); } catch { Application.Quit(); }
        }
    }
}
