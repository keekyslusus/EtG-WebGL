using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace WebShim
{
    /// <summary>
    /// Mods baked into the build (port/ModBaker) come with files they read at runtime (sprites, data):
    /// StreamingAssets/mods/files.pack, made by port/mods/build_pack.py, is unpacked into the in-memory
    /// file system as BepInEx/plugins/&lt;package&gt;/... - the layout r2modman installs. Then BepInEx's
    /// Chainloader (the BepInEx facade, only present in modded builds) creates the plugins once the game's
    /// first scene (LoadingDungeon) has loaded - about when BepInEx's entrypoint (Application..cctor) runs in
    /// the real game. Not in the Boot scene: Mod the Gungeon API's GUI takes its font from the first active
    /// dfGUI control on its first OnGUI, and Boot has none (SGUI then stays broken).
    /// In the editor the same tree is read from etg/mods-runtime.
    /// </summary>
    public static class ModFiles
    {
        private const string Magic = "ETGMODS1";

        public static Type Chainloader
        {
            get { return Type.GetType("BepInEx.Bootstrap.Chainloader, BepInEx", false); }
        }

        public static IEnumerator LoadAndStart(Action<string> setStatus, Action<string> setError)
        {
            Type chainloader = Chainloader;
            if (chainloader == null)
            {
                yield break; // not a modded build
            }
            string root;
            if (Application.isEditor)
            {
                // dataPath = etg/ripped/ExportedProject/Assets
                root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../mods-runtime"));
            }
            else
            {
                root = "/mods";
                setStatus("mods");
                using (UnityWebRequest req = UnityWebRequest.Get(Application.streamingAssetsPath + "/mods/files.pack"))
                {
                    yield return req.SendWebRequest();
                    if (req.isNetworkError || req.isHttpError)
                    {
                        setError("Failed to load mod files: " + req.error);
                        yield break;
                    }
                    try
                    {
                        int count = Unpack(req.downloadHandler.data, root);
                        Debug.Log("[WebShim] Unpacked " + count + " mod files into " + root);
                    }
                    catch (Exception e)
                    {
                        setError("Broken mod files pack: " + e.Message);
                        yield break;
                    }
                }
            }
            s_pluginPath = Path.Combine(root, "BepInEx/plugins");
            SceneManager.sceneLoaded += StartOnFirstGameScene;
        }

        private static string s_pluginPath;

        private static void StartOnFirstGameScene(Scene scene, LoadSceneMode mode)
        {
            if (scene.buildIndex < 1)
            {
                return; // Boot
            }
            SceneManager.sceneLoaded -= StartOnFirstGameScene;
            string gameRoot = Path.Combine(FileShim.PersistentDataPath(), "modded");
            try
            {
                Chainloader.GetMethod("Start", BindingFlags.Public | BindingFlags.Static)
                    .Invoke(null, new object[] { s_pluginPath, gameRoot });
            }
            catch (TargetInvocationException e)
            {
                Debug.LogError("[WebShim] Chainloader failed: " + e.InnerException);
            }
            FileShim.Sync();
        }

        // "ETGMODS1", int32 count, then per file: int32 path length, UTF-8 path, int32 size, bytes.
        // Paths are relative to root, or absolute (/Il2CppData/Resources/*-resources.dat: Unity 2017's
        // WebGL packager leaves out the files IL2CPP reads embedded resources from).
        private static int Unpack(byte[] pack, string root)
        {
            using (var reader = new BinaryReader(new MemoryStream(pack)))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length)) != Magic)
                {
                    throw new Exception("bad magic"); // InvalidDataException is missing from the WebGL profile
                }
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string path = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
                    byte[] data = reader.ReadBytes(reader.ReadInt32());
                    // Absolute entries (IL2CPP's resource files) go where they say.
                    string full = path.StartsWith("/") ? path : Path.Combine(root, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    File.WriteAllBytes(full, data);
                }
                return count;
            }
        }
    }
}
