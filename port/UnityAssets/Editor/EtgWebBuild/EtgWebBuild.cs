using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Written for Unity 2017.4's C# 4 (no string interpolation, no expression-bodied members, no ?.), which
// Unity 6 compiles as is.
public static class EtgWebBuild
{
    internal const string BootScenePath = "Assets/Scenes/Boot.unity";
    // The original game's only built-in scene (level0); everything else comes from bundles.
    internal const string FirstGameScenePath = "Assets/Scenes/LoadingDungeon.unity";
    private const string BundleOutput = "Assets/StreamingAssets/Assets";
    // Relative to Assets/: Assets -> Unity6Project -> ripped -> etg/webbuild6. While the port moves to Unity 6
    // (port/tasks/unity6-migration.md), webbuild/ keeps the working Unity 2017.4 build.
    private const string PlayerOutput = "../../../webbuild6";

    // Recovered from the original bundles with AssetRipper: which scene each scene-bundle held.
    internal static readonly Dictionary<string, string> SceneBundles = new Dictionary<string, string>
    {
        { "Assets/Scenes/tt_foyer.unity", "foyer_001" },
        { "Assets/Scenes/Foyer.unity", "foyer_002" },
        { "Assets/Scenes/Foyer_Coop.unity", "foyer_003" },
        { "Assets/Scenes/tt6.unity", "dungeon_scene_001" },
    };

    [MenuItem("EtG Web/1. Setup Boot Scene + Build Settings")]
    public static void SetupBootScene()
    {
        if (!File.Exists(BootScenePath))
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = new GameObject("Camera").AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            new GameObject("BootLoader").AddComponent<WebShim.BootLoader>();
            EditorSceneManager.SaveScene(scene, BootScenePath);
            Debug.Log("[EtG Web] Created " + BootScenePath);
        }

        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(BootScenePath, true),
            new EditorBuildSettingsScene(FirstGameScenePath, true),
        };
        Debug.Log("[EtG Web] Build settings: Boot -> LoadingDungeon");
    }

    [MenuItem("EtG Web/2. Apply WebGL Player Settings")]
    public static void ApplyPlayerSettings()
    {
        ApplyPlayerSettings(true);
    }

    // fullExceptions: development builds; release builds get the cheaper exception support (below).
    private static void ApplyPlayerSettings(bool fullExceptions)
    {
        PlayerSettings.runInBackground = true;
        PlayerSettings.stripEngineCode = false;
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.WebGL, ScriptingImplementation.IL2CPP);
        // EtG was built against the full .NET 2.0 profile and the mods against .NET Framework; .NET Standard
        // lacks File.Move/ReadAllBytes, Registry, Process etc. that they reference.
        PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.WebGL, ApiCompatibilityLevel.NET_Unity_4_8);

        // AssetRipper decompiles EtG's DX11 shaders into HLSL with DX11-style Texture2D/SamplerState.
        // The WebGL 2 (gles3) compiler accepts that; the WebGL 1 (gles) one fails on every shader.
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, new[] { GraphicsDeviceType.OpenGLES3 });

        // AssetRipper leaves this empty (2017.4's WebGL postprocessor crashed on template.Split(':')[1]).
        PlayerSettings.WebGL.template = "APPLICATION:Default";
        // The heap grows on demand up to the maximum. 2032 MB (2017.4's fixed heap) ran out with the mods on the
        // first floor; 4 GB is wasm32's limit (more would switch the build to wasm64, see the migration notes).
        PlayerSettings.WebGL.initialMemorySize = 512;
        PlayerSettings.WebGL.maximumMemorySize = 4096;
        PlayerSettings.WebGL.memoryGrowthMode = WebGLMemoryGrowthMode.Geometric;
        // EtG logs a lot every frame (e.g. "Unable to find clip 'select_idle'"); building a managed
        // stack trace for each Log/Warning is what makes that expensive in the browser.
        PlayerSettings.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        PlayerSettings.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
        // 2017.4 needed "--js-opts 0" here (fastcomp's JS optimizer); clear it, Emscripten 4 has no such flag.
        PlayerSettings.WebGL.emscriptenArgs = "";
        PlayerSettings.WebGL.dataCaching = true;
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
        // Full support makes il2cpp emit a null check before every member access, a bounds check on every
        // array access and (WebGL can't walk the native stack) a push/pop of its own call stack in every
        // managed method: measured in the browser, the game's code took 24-30 ms a frame in combat. EtG
        // never catches NullReferenceException; its catch blocks (dfGUI bindings, FullInspector, iTween,
        // reflection) wrap explicit throws, which "explicitly thrown only" still delivers. A null access
        // then reads memory instead of throwing, so development builds keep full support to show bugs.
        PlayerSettings.WebGL.exceptionSupport = fullExceptions
            ? WebGLExceptionSupport.FullWithStacktrace
            : WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;

        AssetDatabase.SaveAssets();
        Debug.Log("[EtG Web] Player settings applied: graphics=" +
                  string.Join(",", Array.ConvertAll(PlayerSettings.GetGraphicsAPIs(BuildTarget.WebGL), a => a.ToString())) +
                  " useDefaultAPIs=" + PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.WebGL) +
                  " memory=" + PlayerSettings.WebGL.initialMemorySize + ".." + PlayerSettings.WebGL.maximumMemorySize +
                  " template=" + PlayerSettings.WebGL.template +
                  " api=" + PlayerSettings.GetApiCompatibilityLevel(NamedBuildTarget.WebGL) +
                  " exceptions=" + PlayerSettings.WebGL.exceptionSupport);
    }

    [MenuItem("EtG Web/3. Build AssetBundles (WebGL)")]
    public static void BuildBundlesMenu()
    {
        BuildBundles();
        // The player build copies StreamingAssets only when it runs; bundles built after it (step 3
        // after step 4) would otherwise never reach webbuild and the browser keeps the old shaders.
        string target = Path.GetFullPath(Path.Combine(Application.dataPath, PlayerOutput));
        if (File.Exists(Path.Combine(target, "index.html")))
        {
            CopyBundlesToWebBuild(target);
        }
    }

    public static void BuildBundles()
    {
        foreach (var pair in SceneBundles)
        {
            AssetImporter importer = AssetImporter.GetAtPath(pair.Key);
            if (importer == null)
            {
                throw new Exception("Scene not found: " + pair.Key);
            }
            if (importer.assetBundleName != pair.Value)
            {
                importer.assetBundleName = pair.Value;
                importer.SaveAndReimport();
            }
        }

        Directory.CreateDirectory(BundleOutput);
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            BundleOutput, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.WebGL);
        if (manifest == null)
        {
            throw new Exception("[EtG Web] BuildAssetBundles failed, see console");
        }
        Debug.Log("[EtG Web] Built " + manifest.GetAllAssetBundles().Length + " bundles into " + BundleOutput);
    }

    [MenuItem("EtG Web/Fix Texture Formats (original game formats)")]
    public static void FixTextureFormats()
    {
        // AssetRipper keeps each texture's original format in the .meta (top-level "textureFormat")
        // but imports everything as "Compressed / automatic", so Unity turns EtG's uncompressed
        // pixel art (RGBA32/RGB24/Alpha8) into DXT. That smears the sprites and breaks the game's
        // runtime pixel work (palettes, Resize: "Unsupported texture format - needs to be ARGB32...").
        // Pin the WebGL import format to the original one.
        int changed = 0;
        string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" });
        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
                int original = ReadOriginalTextureFormat(path + ".meta");
                if (importer == null || original <= 0)
                {
                    continue;
                }
                TextureImporterFormat format = (TextureImporterFormat)original;
                bool uncompressed = format == TextureImporterFormat.Alpha8 || format == TextureImporterFormat.RGB24
                    || format == TextureImporterFormat.RGBA32 || format == TextureImporterFormat.ARGB32;
                TextureImporterPlatformSettings webgl = importer.GetPlatformTextureSettings("WebGL");
                if (webgl.overridden && webgl.format == format)
                {
                    continue;
                }
                webgl.overridden = true;
                webgl.format = format;
                webgl.maxTextureSize = importer.maxTextureSize;
                webgl.textureCompression = uncompressed ? TextureImporterCompression.Uncompressed : TextureImporterCompression.Compressed;
                webgl.crunchedCompression = false;
                importer.SetPlatformTextureSettings(webgl);
                AssetDatabase.WriteImportSettingsIfDirty(path);
                AssetDatabase.ImportAsset(path);
                changed++;
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }
        Debug.Log("[EtG Web] Texture formats pinned to originals: " + changed + " of " + guids.Length + " textures changed");
    }

    private static readonly System.Text.RegularExpressions.Regex TopLevelTextureFormat =
        new System.Text.RegularExpressions.Regex(@"^  textureFormat: (-?\d+)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);

    private static int ReadOriginalTextureFormat(string metaPath)
    {
        if (!File.Exists(metaPath))
        {
            return -1;
        }
        var m = TopLevelTextureFormat.Match(File.ReadAllText(metaPath));
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    [MenuItem("EtG Web/Rebuild Bundles Only -> webbuild")]
    public static void RebuildBundlesIntoWebBuild()
    {
        // For asset-only changes (shaders, textures, prefabs inside bundles): no IL2CPP, no player
        // build. BootLoader caches bundles by manifest hash, so browsers re-download only what changed.
        // Assets that live in the player data itself (the Boot/LoadingDungeon scenes and what they
        // reference directly, Resources/, and the Always Included Shaders - Brave/PlayerShader,
        // LitCutoutUber... which Shader.Find returns in preference to bundle copies) still need a
        // full player build.
        string target = Path.GetFullPath(Path.Combine(Application.dataPath, PlayerOutput));
        if (!File.Exists(Path.Combine(target, "index.html")))
        {
            throw new Exception("[EtG Web] No player build in " + target + " yet - run a full build first");
        }
        FixTextureFormats();
        BuildBundles();
        CopyBundlesToWebBuild(target);
    }

    private static void CopyBundlesToWebBuild(string target)
    {
        string source = Path.GetFullPath(BundleOutput);
        string dest = Path.Combine(Path.Combine(target, "StreamingAssets"), "Assets");
        int copied = 0;
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".meta"))
            {
                continue;
            }
            string relative = file.Substring(source.Length).TrimStart('\\', '/');
            string to = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            File.Copy(file, to, true);
            copied++;
        }
        Debug.Log("[EtG Web] Copied " + copied + " bundle files into " + dest + " - reload the page");
    }

    [MenuItem("EtG Web/4. Build WebGL Player (Development)")]
    public static void BuildDevelopment()
    {
        BuildPlayer(BuildOptions.Development);
    }

    [MenuItem("EtG Web/5. Build WebGL Player (Release)")]
    public static void BuildRelease()
    {
        BuildPlayer(BuildOptions.None);
    }

    [MenuItem("EtG Web/Do Everything (1-4)")]
    public static void BuildAll()
    {
        SetupBootScene();
        ApplyPlayerSettings();
        FixTextureFormats();
        BuildBundles();
        BuildDevelopment();
    }

    private static void BuildPlayer(BuildOptions options)
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
        {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL);
        }
        // Always re-apply so a stale project setting can't silently produce a WebGL 1 / asm.js build.
        ApplyPlayerSettings((options & BuildOptions.Development) != 0);
        string output = Path.GetFullPath(Path.Combine(Application.dataPath, PlayerOutput));
        BuildReport report = BuildPipeline.BuildPlayer(new[] { BootScenePath, FirstGameScenePath }, output, BuildTarget.WebGL, options);
        if (report.summary.result != BuildResult.Succeeded)
        {
            // A failed build leaves the previous one in the output folder, which is easy to mistake for the new one.
            string summary = report.summary.result + ", " + report.summary.totalErrors + " errors";
            EditorUtility.DisplayDialog("EtG Web: build failed",
                "The WebGL player was not built; " + output + " still has the previous build.\n\n" + summary +
                "\n\nDetails: %LOCALAPPDATA%\\Unity\\Editor\\Editor.log.", "OK");
            throw new Exception("[EtG Web] Build failed: " + summary);
        }
        CopySite(output);
        Debug.Log("[EtG Web] WebGL build written to " + output);
    }

    // The page around the player (port/site; see install.py there, which does the same by hand).
    // Replaces the index.html that the Default template writes on every player build.
    private const string SiteDir = "../../../port/site";

    [MenuItem("EtG Web/Copy Site Page -> webbuild")]
    public static void CopySiteToWebBuild()
    {
        CopySite(Path.GetFullPath(Path.Combine(Application.dataPath, PlayerOutput)));
    }

    private static void CopySite(string output)
    {
        string site = Path.GetFullPath(Path.Combine(Application.dataPath, SiteDir));
        foreach (string pattern in new[] { "*.html", "*.js", "*.css" })
        {
            foreach (string file in Directory.GetFiles(site, pattern))
            {
                File.Copy(file, Path.Combine(output, Path.GetFileName(file)), true);
            }
        }
        string assets = Path.Combine(output, "assets");
        Directory.CreateDirectory(assets);
        foreach (string file in Directory.GetFiles(Path.Combine(site, "assets")))
        {
            File.Copy(file, Path.Combine(assets, Path.GetFileName(file)), true);
        }
        WriteBuildInfo(output);
        Debug.Log("[EtG Web] Site page copied from " + site);
    }

    // etg-build.js: the player's file names for the page (Unity 6 names them after the output folder, e.g.
    // Build/webbuild6.loader.js, .data.gz) and the names createUnityInstance wants. port/site/install.py writes the same.
    private static readonly string[][] BuildFileKinds =
    {
        new[] { "loader", @"\.loader\.js$" },
        new[] { "framework", @"\.framework\.js(\.\w+)?$" },
        new[] { "data", @"\.data(\.\w+)?$" },
        new[] { "code", @"\.wasm(\.\w+)?$" },
    };

    private static void WriteBuildInfo(string output)
    {
        string[] names = Array.ConvertAll(Directory.GetFiles(Path.Combine(output, "Build")), f => Path.GetFileName(f));
        Array.Sort(names, StringComparer.Ordinal);
        StringBuilder js = new StringBuilder("// Written by EtgWebBuild.CopySite / port/site/install.py: the player's files in Build/.\n");
        js.Append("window.EtgBuild = {\n");
        foreach (string[] kind in BuildFileKinds)
        {
            string[] found = Array.FindAll(names, n => Regex.IsMatch(n, kind[1]));
            if (found.Length != 1)
            {
                throw new Exception("[EtG Web] Build/ in " + output + ": expected one " + kind[0] + " file, found " + found.Length);
            }
            js.Append("  \"").Append(kind[0]).Append("\": \"Build/").Append(found[0]).Append("\",\n");
        }
        js.Append("  \"companyName\": ").Append(JsString(PlayerSettings.companyName)).Append(",\n");
        js.Append("  \"productName\": ").Append(JsString(PlayerSettings.productName)).Append(",\n");
        js.Append("  \"productVersion\": ").Append(JsString(PlayerSettings.bundleVersion)).Append("\n};\n");
        File.WriteAllText(Path.Combine(output, "etg-build.js"), js.ToString(), new UTF8Encoding(false));
    }

    private static string JsString(string s)
    {
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
