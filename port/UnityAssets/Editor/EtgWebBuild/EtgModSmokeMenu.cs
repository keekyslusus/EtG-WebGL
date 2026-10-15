using System.IO;
using UnityEditor;
using UnityEngine;

// EtG Web -> Mod Smoke Test: Play From Boot with EtgModSmoke.SmokeTest running (port/README.md, Mods ->
// Smoke test). The phases go to the runtime through Temp/modsmoke.pending (Play Mode reloads the script
// domain); port/build/modsmoke/only.txt, if present, limits the run to the IDs / names listed in it.
// Unattended (port/modsmoke.sh): port/build/modsmoke/request (phases line, then quit 0/1) starts a run as soon as the
// editor is idle after any script domain load, so it doesn't matter whether the editor first runs an old build of
// this script and recompiles. Also: Unity.exe -executeMethod EtgModSmokeMenu.RunFromCommandLine
//   [-modSmoke items,shop|all] [-modSmokeQuit]   (quit: the editor exits once the run is over).
// Same C# 4 restrictions as EtgWebBuild.cs.
[InitializeOnLoad]
public static class EtgModSmokeMenu
{
    private const string Root = "EtG Web/Mod Smoke Test/";
    // EditorPrefs: survive the domain reloads of entering and leaving Play Mode
    private const string QuitKey = "EtgWeb.ModSmoke.QuitWhenDone";
    private const string DoneKey = "EtgWeb.ModSmoke.Done";
    private const string PendingKey = "EtgWeb.ModSmoke.CommandLinePhases"; // earlier builds of this script

    private static string RequestFile
    {
        get { return Path.Combine(EtgModSmoke.SmokeTest.DefaultReportDir, "request"); } // not Temp/: the editor clears it on startup
    }

    static EtgModSmokeMenu()
    {
        // Runs again after the domain reload of entering Play Mode, before the test starts.
        EtgModSmoke.SmokeTest.Finished = OnFinished;
        EtgModSmoke.SmokeTest.Resumable = EditorPrefs.GetBool(QuitKey, false);
        EditorApplication.update += QuitIfDone;
        // a requested start survives the script recompile / domain reload the editor may do on startup
        EditorPrefs.DeleteKey(PendingKey);
        if (File.Exists(RequestFile) && !EditorApplication.isPlayingOrWillChangePlaymode)
        {
            ScheduleStart();
        }
    }

    public static void RunFromCommandLine()
    {
        string phases = "all";
        bool quit = false;
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-modSmoke" && i + 1 < args.Length)
            {
                phases = args[i + 1];
            }
            else if (args[i] == "-modSmokeQuit")
            {
                quit = true;
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(RequestFile));
        File.WriteAllText(RequestFile, phases + "\n" + (quit ? "1" : "0"));
        ScheduleStart();
    }

    // -executeMethod runs while the editor is still starting up: begin once it is idle
    private static void ScheduleStart()
    {
        int ticks = 0;
        EditorApplication.CallbackFunction start = null;
        start = delegate
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || ++ticks < 30)
            {
                return;
            }
            EditorApplication.update -= start;
            if (!File.Exists(RequestFile) || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }
            string[] request = File.ReadAllText(RequestFile).Split('\n');
            File.Delete(RequestFile);
            EditorPrefs.SetBool(DoneKey, false);
            Run(request[0].Trim(), request.Length > 1 && request[1].Trim() == "1");
        };
        EditorApplication.update += start;
    }

    // After a command-line run with -modSmokeQuit: exit once Play Mode is over.
    private static void QuitIfDone()
    {
        if (EditorPrefs.GetBool(DoneKey, false) && !EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorPrefs.SetBool(DoneKey, false);
            if (EditorPrefs.GetBool(QuitKey, false))
            {
                EditorPrefs.SetBool(QuitKey, false);
                Debug.Log("[EtG Web] Mod smoke test over, exiting (-modSmokeQuit)");
                EditorApplication.Exit(0);
            }
        }
    }

    private static string OnlyFile
    {
        get { return Path.Combine(EtgModSmoke.SmokeTest.DefaultReportDir, "only.txt"); }
    }

    [MenuItem(Root + "Everything", false, 2)]
    public static void RunAll() { Run("all"); }

    [MenuItem(Root + "Items + Shop", false, 3)]
    public static void RunItems() { Run(EtgModSmoke.SmokeTest.PhaseItems + "," + EtgModSmoke.SmokeTest.PhaseShop); }

    [MenuItem(Root + "Enemies", false, 4)]
    public static void RunEnemies() { Run(EtgModSmoke.SmokeTest.PhaseEnemies); }

    [MenuItem(Root + "Floors + Flows", false, 5)]
    public static void RunFloors() { Run(EtgModSmoke.SmokeTest.PhaseFloors + "," + EtgModSmoke.SmokeTest.PhaseFlows); }

    [MenuItem(Root + "Show Report", false, 20)]
    public static void ShowReport()
    {
        string md = Path.Combine(EtgModSmoke.SmokeTest.DefaultReportDir, "report.md");
        if (File.Exists(md))
        {
            EditorUtility.RevealInFinder(md);
        }
        else
        {
            Debug.LogWarning("[EtG Web] No report yet: " + md);
        }
    }

    private static void Run(string phases)
    {
        Run(phases, false);
    }

    private static void Run(string phases, bool quitWhenDone)
    {
        EditorPrefs.SetBool(QuitKey, quitWhenDone);
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[EtG Web] Stop Play Mode first");
            return;
        }
        string filter = File.Exists(OnlyFile) ? File.ReadAllText(OnlyFile) : "";
        string pending = EtgModSmoke.SmokeTest.PendingFile;
        Directory.CreateDirectory(Path.GetDirectoryName(pending));
        File.WriteAllText(pending, phases + "\n" + filter);
        Debug.Log("[EtG Web] Mod smoke test: " + phases + (filter.Trim().Length > 0 ? " (only.txt: " + filter.Trim().Replace("\n", ", ") + ")" : ""));
        EtgWebPlayMode.PlayFromBoot();
        if (!EditorApplication.isPlayingOrWillChangePlaymode)
        {
            File.Delete(pending); // cancelled (save scene dialog)
        }
    }

    private static void OnFinished(string report)
    {
        Debug.Log("[EtG Web] Mod smoke test finished: " + report);
        EditorPrefs.SetBool(DoneKey, true);
        EditorApplication.isPlaying = false;
    }
}
