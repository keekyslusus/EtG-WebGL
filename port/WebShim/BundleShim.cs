using System.Collections.Generic;
using UnityEngine;

namespace WebShim
{
    /// <summary>
    /// ResourceManager.LoadAssetBundle calls AssetBundle.LoadFromFile synchronously,
    /// which can't read StreamingAssets on WebGL. BootLoader downloads every bundle
    /// up front and registers it here; the patcher redirects LoadFromFile to us.
    /// </summary>
    public static class BundleShim
    {
        /// <summary>Every bundle the game ships, relative to StreamingAssets/Assets/.</summary>
        public static readonly string[] AllBundles =
        {
            "shared_base_001",
            "shared_auto_001",
            "shared_auto_002",
            "brave_resources_001",
            "enemies_base_001",
            "encounters_base_001",
            "flows_base_001",
            "foyer_001",
            "foyer_002",
            "foyer_003",
            "dungeon_scene_001",
            "dungeons/base_foyer",
            "dungeons/base_tutorial",
            "dungeons/base_castle",
            "dungeons/base_sewer",
            "dungeons/base_gungeon",
            "dungeons/base_cathedral",
            "dungeons/base_mines",
            "dungeons/base_resourcefulrat",
            "dungeons/base_catacombs",
            "dungeons/base_nakatomi",
            "dungeons/base_forge",
            "dungeons/base_bullethell",
            "dungeons/finalscenario_bullet",
            "dungeons/finalscenario_convict",
            "dungeons/finalscenario_coop",
            "dungeons/finalscenario_guide",
            "dungeons/finalscenario_pilot",
            "dungeons/finalscenario_robot",
            "dungeons/finalscenario_soldier",
        };

        private static readonly Dictionary<string, AssetBundle> s_bundles = new Dictionary<string, AssetBundle>();

        public static void Register(string name, AssetBundle bundle)
        {
            s_bundles[Normalize(name)] = bundle;
        }

        public static bool IsRegistered(string name)
        {
            return s_bundles.ContainsKey(Normalize(name));
        }

        /// <summary>Drop-in replacement for AssetBundle.LoadFromFile(string).</summary>
        public static AssetBundle LoadFromFile(string path)
        {
            string name = ToBundleName(path);
            AssetBundle bundle;
            if (s_bundles.TryGetValue(name, out bundle))
            {
                return bundle;
            }
            if (Application.platform == RuntimePlatform.WebGLPlayer)
            {
                Debug.LogError("[WebShim] Bundle was not preloaded: " + name + " (" + path + ")");
                return null;
            }
            // Editor / desktop: real file access works.
            bundle = AssetBundle.LoadFromFile(path);
            if (bundle != null)
            {
                s_bundles[name] = bundle;
            }
            return bundle;
        }

        /// <summary>".../StreamingAssets/Assets/dungeons/base_castle" -> "dungeons/base_castle".</summary>
        private static string ToBundleName(string path)
        {
            string p = Normalize(path);
            const string marker = "/assets/";
            int i = p.LastIndexOf(marker);
            // LastIndexOf would hit a "/assets/" inside the bundle name if one existed;
            // none of EtG's bundle names contain it, so this is safe.
            return i >= 0 ? p.Substring(i + marker.Length) : p;
        }

        private static string Normalize(string s)
        {
            return s.Replace('\\', '/').ToLowerInvariant();
        }
    }
}
