using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace WebShim
{
    /// <summary>
    /// Lives alone in the Boot scene (build index 0). Downloads every AssetBundle
    /// (cached in IndexedDB by Unity after the first visit), registers them with
    /// BundleShim, then loads the game's original first scene (build index 1).
    /// </summary>
    public class BootLoader : MonoBehaviour
    {
        // Used only if the bundle manifest can't be read; otherwise each bundle is cached under
        // its content hash, so a rebuilt bundle is re-downloaded and an unchanged one isn't.
        public uint CacheVersion = 1;

        private int m_done;
        private int m_total = BundleShim.AllBundles.Length;
        private float m_currentProgress;
        private string m_current = "";
        private string m_error;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            AmbientProbeShim.Install();
            DebugKeys.Install();
            string root = Application.streamingAssetsPath + "/Assets/";

            if (BundleShim.Simulation && Application.isEditor)
            {
                // Editor Play Mode: nothing to download, assets come straight from the project.
                // LoadFromFile registers a placeholder per bundle (which == calls null, so check
                // registration instead).
                string[] all = BundleShim.AllBundles;
                for (int i = 0; i < all.Length; i++)
                {
                    m_current = all[i];
                    BundleShim.LoadFromFile(root + all[i]);
                    if (!BundleShim.IsRegistered(all[i]))
                    {
                        m_error = "Nothing in the project is tagged with bundle " + all[i];
                        yield break;
                    }
                    m_done++;
                }
                Debug.Log("[WebShim] Bundle simulation: " + all.Length + " bundles served from the AssetDatabase");
                yield return StartMods();
                if (m_error != null)
                {
                    yield break;
                }
                SceneManager.LoadScene(1);
                yield break;
            }

            // BuildAssetBundles writes a manifest bundle named after the output folder ("Assets").
            AssetBundleManifest manifest = null;
            m_current = "manifest";
            using (UnityWebRequest req = UnityWebRequestAssetBundle.GetAssetBundle(root + "Assets"))
            {
                yield return req.SendWebRequest();
                AssetBundle manifestBundle = req.result != UnityWebRequest.Result.Success ? null : DownloadHandlerAssetBundle.GetContent(req);
                if (manifestBundle != null)
                {
                    manifest = manifestBundle.LoadAsset<AssetBundleManifest>("AssetBundleManifest");
                    manifestBundle.Unload(false);
                }
                if (manifest == null)
                {
                    Debug.LogWarning("[WebShim] No bundle manifest, caching bundles under version " + CacheVersion);
                }
            }

            // The game's bundles, then any other the manifest lists: the baked mods' own (built from the
            // project like the game's, see port/mods/README.md), which mods load from their plugin folder.
            List<string> names = new List<string>(BundleShim.AllBundles);
            if (manifest != null)
            {
                foreach (string extra in manifest.GetAllAssetBundles())
                {
                    if (!names.Contains(extra))
                    {
                        names.Add(extra);
                    }
                }
            }
            m_total = names.Count;
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                m_current = name;
                m_currentProgress = 0f;
                if (BundleShim.IsRegistered(name))
                {
                    m_done++;
                    continue;
                }
                using (UnityWebRequest req = manifest != null
                    ? UnityWebRequestAssetBundle.GetAssetBundle(root + name, manifest.GetAssetBundleHash(name), 0u)
                    : UnityWebRequestAssetBundle.GetAssetBundle(root + name, CacheVersion, 0u))
                {
                    AsyncOperation op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        m_currentProgress = req.downloadProgress;
                        yield return null;
                    }
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        m_error = "Failed to load " + name + ": " + req.error;
                        Debug.LogError("[WebShim] " + m_error);
                        yield break;
                    }
                    AssetBundle bundle = DownloadHandlerAssetBundle.GetContent(req);
                    if (bundle == null)
                    {
                        m_error = "Bundle is empty or broken: " + name;
                        Debug.LogError("[WebShim] " + m_error);
                        yield break;
                    }
                    BundleShim.Register(name, bundle);
                }
                m_done++;
            }
            yield return StartMods();
            if (m_error != null)
            {
                yield break;
            }
            m_current = "Starting...";
            yield return null;
            SceneManager.LoadScene(1);
        }

        // Baked mods (if any) start before the game's first scene, like BepInEx's chainloader.
        private IEnumerator StartMods()
        {
            return ModFiles.LoadAndStart(s => m_current = s, e =>
            {
                m_error = e;
                Debug.LogError("[WebShim] " + e);
            });
        }

        private void OnGUI()
        {
            int total = m_total;
            float progress = (m_done + m_currentProgress) / total;
            float w = Screen.width * 0.6f;
            float x = (Screen.width - w) / 2f;
            float y = Screen.height / 2f;

            GUI.color = Color.white;
            GUI.Label(new Rect(x, y - 40f, w, 24f), "Enter the Gungeon - loading " + (m_done + 1 > total ? total : m_done + 1) + "/" + total + "  " + m_current);
            GUI.Box(new Rect(x, y, w, 20f), GUIContent.none);
            GUI.color = new Color(0.9f, 0.75f, 0.2f);
            GUI.DrawTexture(new Rect(x + 2f, y + 2f, (w - 4f) * progress, 16f), Texture2D.whiteTexture);
            if (m_error != null)
            {
                GUI.color = Color.red;
                GUI.Label(new Rect(x, y + 30f, w, 60f), m_error);
            }
        }
    }
}
