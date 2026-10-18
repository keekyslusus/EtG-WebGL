using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Play Mode without AssetBundles: the bundles in StreamingAssets are built for WebGL and the
// editor refuses to load them, so WebShim.BundleShim serves bundle assets from the AssetDatabase
// instead (by their assetBundleName tags). Same C# 4 restrictions as EtgWebBuild.cs.
[InitializeOnLoad]
public static class EtgWebPlayMode
{
    private const string SimulateKey = "EtgWeb.SimulateBundles";
    private const string SimulateMenu = "EtG Web/Simulate Bundles In Play Mode";

    // Runs after every domain reload, including the one on entering Play Mode, which resets the
    // runtime DLL's statics - so this is what turns the simulation on inside Play Mode.
    static EtgWebPlayMode()
    {
        // Unity 6 compiles a shader variant the editor hasn't used yet in the background and draws with a cyan
        // placeholder meanwhile: a one-frame cyan flash the first time something appears (the pause menu...). The
        // player has every variant precompiled; in the editor wait for the compile instead, as 2017.4 did (this also
        // keeps the placeholder out of abtest / variant-selection renders).
        if (EditorSettings.asyncShaderCompilation)
        {
            EditorSettings.asyncShaderCompilation = false;
        }
        WebShim.BundleShim.Simulation = Simulate;
        WebShim.BundleShim.SimulationStarting = PreloadForConstructors;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    // On by default: without it Play Mode can't load any bundle at all.
    public static bool Simulate
    {
        get { return EditorPrefs.GetBool(SimulateKey, true); }
        set
        {
            EditorPrefs.SetBool(SimulateKey, value);
            WebShim.BundleShim.Simulation = value;
        }
    }

    [MenuItem("EtG Web/Play From Boot", false, 0)]
    public static void PlayFromBoot()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            return;
        }
        Simulate = true;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            return;
        }
        // Boot (0) -> LoadingDungeon (1), exactly as in the WebGL player; creates Boot.unity if needed.
        EtgWebBuild.SetupBootScene();
        AddBundleScenesToBuildSettings();
        EditorSceneManager.OpenScene(EtgWebBuild.BootScenePath, OpenSceneMode.Single);
        // Unity 2017.4 needed Edit -> Graphics Emulation -> WebGL 2.0 here (it defaulted to WebGL 1.0 caps); Unity 6
        // has no graphics emulation, the editor runs with its own device's caps (GLES 3.0 via open-gles30.ps1).
        EditorApplication.isPlaying = true;
    }

    [MenuItem(SimulateMenu, false, 1)]
    public static void ToggleSimulation()
    {
        Simulate = !Simulate;
        Debug.Log("[EtG Web] Bundle simulation in Play Mode: " + (Simulate ? "on" : "off"));
    }

    [MenuItem(SimulateMenu, true)]
    private static bool ToggleSimulationValidate()
    {
        Menu.SetChecked(SimulateMenu, Simulate);
        return true;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode && Simulate)
        {
            AddBundleScenesToBuildSettings();
        }
        else if (state == PlayModeStateChange.ExitingPlayMode)
        {
            WebShim.BundleShim.StopSimulation();
        }
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            RemoveBundleScenesFromBuildSettings();
        }
    }

    // Assets mods load in MonoBehaviour constructors, where Unity 6's editor refuses AssetDatabase (BundleShim,
    // "Simulation"): the ones listed next to this script, plus what BundleShim logged as refused in earlier sessions.
    // BootLoader calls this (BundleShim.StartSimulation) before the game loads anything.
    private const string PreloadList = "Assets/Editor/EtgWebBuild/PlayModePreload.txt";
    private const string RefusedList = "Library/EtgWeb/PlayModePreload.txt";

    private static void PreloadForConstructors()
    {
        WebShim.BundleShim.PrepareSimulation(System.IO.Path.GetFullPath(RefusedList));
        int loaded = 0, listed = 0;
        foreach (string file in new[] { PreloadList, RefusedList })
        {
            if (!System.IO.File.Exists(file))
            {
                continue;
            }
            foreach (string line in System.IO.File.ReadAllLines(file))
            {
                string[] parts = line.Split('\t');
                if (parts.Length != 3 || line.StartsWith("#"))
                {
                    continue;
                }
                listed++;
                System.Type type = System.Type.GetType(parts[2]);
                if (type != null && WebShim.BundleShim.Preload(parts[0], parts[1], type))
                {
                    loaded++;
                }
                else
                {
                    Debug.LogWarning("[EtG Web] Play Mode preload failed: " + line.Replace('\t', ' '));
                }
            }
        }
        Debug.Log("[EtG Web] Play Mode: bundle index ready, " + loaded + " of " + listed + " assets preloaded for mod constructors");
    }

    // The game loads tt_foyer/Foyer/tt6 by path (from GetAllScenePaths) and by name; in the player
    // that works because their bundle is loaded, in Play Mode only for scenes in the build settings.
    // They are added just for the Play session - the WebGL build passes its own scene list anyway.
    private static void AddBundleScenesToBuildSettings()
    {
        List<EditorBuildSettingsScene> scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        bool changed = false;
        foreach (string path in EtgWebBuild.SceneBundles.Keys)
        {
            int i = scenes.FindIndex(delegate(EditorBuildSettingsScene s) { return s.path == path; });
            if (i < 0)
            {
                scenes.Add(new EditorBuildSettingsScene(path, true));
                changed = true;
            }
            else if (!scenes[i].enabled)
            {
                scenes[i] = new EditorBuildSettingsScene(path, true);
                changed = true;
            }
        }
        if (changed)
        {
            EditorBuildSettings.scenes = scenes.ToArray();
            Debug.Log("[EtG Web] Bundle scenes added to the build settings for Play Mode");
        }
    }

    private static void RemoveBundleScenesFromBuildSettings()
    {
        List<EditorBuildSettingsScene> scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        int removed = scenes.RemoveAll(delegate(EditorBuildSettingsScene s) { return EtgWebBuild.SceneBundles.ContainsKey(s.path); });
        if (removed > 0)
        {
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
