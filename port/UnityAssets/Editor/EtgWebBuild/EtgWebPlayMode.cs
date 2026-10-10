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
    private const string GraphicsEmulationMenu = "Edit/Graphics Emulation/WebGL 2.0";

    // Runs after every domain reload, including the one on entering Play Mode, which resets the
    // runtime DLL's statics - so this is what turns the simulation on inside Play Mode.
    static EtgWebPlayMode()
    {
        WebShim.BundleShim.Simulation = Simulate;
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
        // With the WebGL target active the editor defaults to "WebGL 1.0" emulation (GLES2 caps,
        // shader level 30); the player runs WebGL 2 only, so pick shaders/caps the way it does.
        if (!EditorApplication.ExecuteMenuItem(GraphicsEmulationMenu))
        {
            Debug.LogWarning("[EtG Web] Menu item not found: " + GraphicsEmulationMenu);
        }
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
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            RemoveBundleScenesFromBuildSettings();
        }
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
