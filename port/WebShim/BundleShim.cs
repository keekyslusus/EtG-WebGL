using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using UnityEngine;

namespace WebShim
{
    /// <summary>
    /// ResourceManager.LoadAssetBundle calls AssetBundle.LoadFromFile synchronously,
    /// which can't read StreamingAssets on WebGL. BootLoader downloads every bundle
    /// up front and registers it here; the patcher redirects LoadFromFile to us.
    ///
    /// Editor Play Mode can't load the WebGL-built bundles, so with <see cref="Simulation"/> on,
    /// LoadFromFile returns a placeholder AssetBundle (no native object behind it) and every
    /// instance AssetBundle call in the game - also redirected here by the patcher - is answered
    /// from the project's own assets via UnityEditor.AssetDatabase and their assetBundleName tags.
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

        /// <summary>
        /// Set by the editor-only EtgWebBuild ([InitializeOnLoad], so again after every domain reload).
        /// Nothing sets it in a player, so the AssetDatabase code below never runs there.
        /// </summary>
        public static bool Simulation;

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
            if (Simulation && Application.isEditor)
            {
                bundle = CreatePlaceholder(name);
                if ((object)bundle != null)
                {
                    s_bundles[name] = bundle;
                }
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

        // ---- Instance AssetBundle API: the patcher turns bundle.X(args) into BundleShim.X(bundle, args) ----

        public static T LoadAsset<T>(AssetBundle bundle, string name) where T : UnityEngine.Object
        {
            string simulated = SimulatedName(bundle);
            return simulated == null ? bundle.LoadAsset<T>(name) : SimulatedLoad(simulated, name, typeof(T)) as T;
        }

        public static UnityEngine.Object LoadAsset(AssetBundle bundle, string name)
        {
            string simulated = SimulatedName(bundle);
            return simulated == null ? bundle.LoadAsset(name) : SimulatedLoad(simulated, name, typeof(UnityEngine.Object));
        }

        public static UnityEngine.Object LoadAsset(AssetBundle bundle, string name, Type type)
        {
            string simulated = SimulatedName(bundle);
            return simulated == null ? bundle.LoadAsset(name, type) : SimulatedLoad(simulated, name, type);
        }

        public static string[] GetAllScenePaths(AssetBundle bundle)
        {
            string simulated = SimulatedName(bundle);
            if (simulated == null)
            {
                return bundle.GetAllScenePaths();
            }
            // Project paths ("Assets/Scenes/tt_foyer.unity"). SceneManager only loads them if they are
            // in the build settings; EtgWebBuild adds them there for the duration of Play Mode.
            List<string> scenes = new List<string>();
            foreach (string path in EditorAssets.PathsInBundle(simulated))
            {
                if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    scenes.Add(path);
                }
            }
            return scenes.ToArray();
        }

        // ---- Simulation ----

        // Placeholders have no native object, so UnityEngine.Object's == calls them null and its
        // Equals/GetHashCode can't tell two of them apart: key them by reference.
        private static readonly Dictionary<AssetBundle, string> s_placeholders = new Dictionary<AssetBundle, string>(new ReferenceComparer());
        // bundle name -> lower-case key (full path or file name, each with and without extension) -> asset paths
        private static readonly Dictionary<string, Dictionary<string, List<string>>> s_simIndex = new Dictionary<string, Dictionary<string, List<string>>>();

        /// <summary>Simulated bundle name, or null for a real bundle. Throws on null like the original callvirt.</summary>
        private static string SimulatedName(AssetBundle bundle)
        {
            if ((object)bundle == null)
            {
                throw new NullReferenceException("AssetBundle is null");
            }
            string name;
            return s_placeholders.TryGetValue(bundle, out name) ? name : null;
        }

        private static AssetBundle CreatePlaceholder(string name)
        {
            if (Array.IndexOf(EditorAssets.AllBundleNames(), name) < 0)
            {
                Debug.LogError("[WebShim] No assets are tagged with bundle " + name + " - nothing to simulate");
                return null;
            }
            AssetBundle bundle = (AssetBundle)FormatterServices.GetUninitializedObject(typeof(AssetBundle));
            s_placeholders[bundle] = name;
            return bundle;
        }

        private static UnityEngine.Object SimulatedLoad(string bundleName, string assetName, Type type)
        {
            Dictionary<string, List<string>> index;
            if (!s_simIndex.TryGetValue(bundleName, out index))
            {
                index = new Dictionary<string, List<string>>();
                foreach (string path in EditorAssets.PathsInBundle(bundleName))
                {
                    string lower = path.ToLowerInvariant();
                    int slash = lower.LastIndexOf('/');
                    int dot = lower.LastIndexOf('.');
                    string noExtension = dot > slash ? lower.Substring(0, dot) : lower;
                    AddToIndex(index, lower, path);
                    AddToIndex(index, noExtension, path);
                    AddToIndex(index, noExtension.Substring(slash + 1), path);
                    AddToIndex(index, lower.Substring(slash + 1), path); // "BulletMan.prefab" (EnemyDatabaseEntry)
                }
                s_simIndex[bundleName] = index;
            }
            // Like a real bundle: full asset path or file name, extension optional, case-insensitive.
            List<string> candidates;
            if (index.TryGetValue(Normalize(assetName), out candidates))
            {
                foreach (string path in candidates)
                {
                    UnityEngine.Object asset = EditorAssets.LoadAssetAtPath(path, type);
                    if (asset != null)
                    {
                        return asset;
                    }
                }
            }
            Debug.LogWarning("[WebShim] Simulated bundle " + bundleName + " has no " + type.Name + " named " + assetName);
            return null;
        }

        private static void AddToIndex(Dictionary<string, List<string>> index, string key, string path)
        {
            List<string> list;
            if (!index.TryGetValue(key, out list))
            {
                index[key] = list = new List<string>();
            }
            if (!list.Contains(path))
            {
                list.Add(path);
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<AssetBundle>
        {
            public bool Equals(AssetBundle x, AssetBundle y) { return ReferenceEquals(x, y); }
            public int GetHashCode(AssetBundle obj) { return RuntimeHelpers.GetHashCode(obj); }
        }

        /// <summary>UnityEditor.AssetDatabase through reflection: UnityEditor.dll doesn't exist in a player.</summary>
        private static class EditorAssets
        {
            private static Func<string, string[]> s_pathsInBundle;
            private static Func<string, Type, UnityEngine.Object> s_loadAssetAtPath;
            private static Func<string[]> s_allBundleNames;

            public static string[] PathsInBundle(string bundleName)
            {
                if (s_pathsInBundle == null)
                {
                    s_pathsInBundle = Bind<Func<string, string[]>>("GetAssetPathsFromAssetBundle", typeof(string));
                }
                return s_pathsInBundle(bundleName);
            }

            public static UnityEngine.Object LoadAssetAtPath(string path, Type type)
            {
                if (s_loadAssetAtPath == null)
                {
                    s_loadAssetAtPath = Bind<Func<string, Type, UnityEngine.Object>>("LoadAssetAtPath", typeof(string), typeof(Type));
                }
                return s_loadAssetAtPath(path, type);
            }

            public static string[] AllBundleNames()
            {
                if (s_allBundleNames == null)
                {
                    s_allBundleNames = Bind<Func<string[]>>("GetAllAssetBundleNames");
                }
                return s_allBundleNames();
            }

            private static TDelegate Bind<TDelegate>(string method, params Type[] parameters) where TDelegate : class
            {
                Type assetDatabase = Type.GetType("UnityEditor.AssetDatabase, UnityEditor");
                MethodInfo info = assetDatabase == null ? null
                    : assetDatabase.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, parameters, null);
                if (info == null)
                {
                    throw new InvalidOperationException("[WebShim] Bundle simulation needs UnityEditor.AssetDatabase." + method);
                }
                return (TDelegate)(object)Delegate.CreateDelegate(typeof(TDelegate), info);
            }
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
