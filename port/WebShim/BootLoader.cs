using System.Collections;
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
        // Bump to force browsers to re-download bundles after a rebuild.
        public uint CacheVersion = 1;

        private int m_done;
        private float m_currentProgress;
        private string m_current = "";
        private string m_error;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            string root = Application.streamingAssetsPath + "/Assets/";
            string[] names = BundleShim.AllBundles;
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                m_current = name;
                m_currentProgress = 0f;
                if (BundleShim.IsRegistered(name))
                {
                    m_done++;
                    continue;
                }
                using (UnityWebRequest req = UnityWebRequest.GetAssetBundle(root + name, CacheVersion, 0u))
                {
                    AsyncOperation op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        m_currentProgress = req.downloadProgress;
                        yield return null;
                    }
                    if (req.isNetworkError || req.isHttpError)
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
            m_current = "Starting...";
            yield return null;
            SceneManager.LoadScene(1);
        }

        private void OnGUI()
        {
            int total = BundleShim.AllBundles.Length;
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
