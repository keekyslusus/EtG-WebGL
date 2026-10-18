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

        /// <summary>
        /// Drop-in replacement for AssetBundle.LoadFromStream(Stream). Mods use it for bundles inside a zip
        /// (Expand the Gungeon when installed zipped); those are built for Windows, and the web build's own
        /// copies come through LoadFromFile.
        /// </summary>
        public static AssetBundle LoadFromStream(System.IO.Stream stream)
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer || Simulation && Application.isEditor)
            {
                Debug.LogError("[WebShim] AssetBundle.LoadFromStream: only bundles built into the web build can be loaded");
                return null;
            }
            return AssetBundle.LoadFromStream(stream);
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

        public static void Unload(AssetBundle bundle, bool unloadAllLoadedObjects)
        {
            string simulated = SimulatedName(bundle);
            if (simulated == null)
            {
                bundle.Unload(unloadAllLoadedObjects);
                return;
            }
            // A placeholder has nothing to unload; forget it so LoadFromFile makes a new one.
            s_placeholders.Remove(bundle);
            s_bundles.Remove(simulated);
        }

        public static string[] GetAllAssetNames(AssetBundle bundle)
        {
            string simulated = SimulatedName(bundle);
            if (simulated == null)
            {
                return bundle.GetAllAssetNames();
            }
            List<string> names = new List<string>();
            foreach (string path in EditorAssets.PathsInBundle(simulated))
            {
                names.Add(path.ToLowerInvariant());
            }
            return names.ToArray();
        }

        /// <summary>
        /// BuildTarget written into the AssetBundles Alexandria's PrefabAPI builds at runtime (it writes
        /// StandaloneWindows64 = 19); a WebGL player only loads bundles made for WebGL (20).
        /// </summary>
        public static int RuntimeBundleTargetPlatform()
        {
            return Application.platform == RuntimePlatform.WebGLPlayer ? 20 : 19;
        }

        // ---- Simulation ----

        // Placeholders have no native object; on their own, UnityEngine.Object's == calls them null, which
        // breaks code that checks a loaded bundle (Alexandria's StaticReferences.Init drops every one). So
        // each borrows the instance ID of its own hidden live object (Object.IsNativeObjectAlive looks the ID
        // up when there is no native pointer; == between two objects compares the IDs): == null is false,
        // and no native call ever sees a placeholder, because every instance AssetBundle call goes through
        // this class. Placeholders share Object's GetHashCode quirks: key them by reference.
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
            AssetBundle bundle = (AssetBundle)FormatterServices.GetUninitializedObject(typeof(AssetBundle));
            if (!s_playing)
            {
                // Outside Play Mode only an import runs a mod component's constructor; it gets nothing (SimulatedLoad).
                s_placeholders[bundle] = name;
                return bundle;
            }
            if (Array.IndexOf(EditorAssets.AllBundleNames(), name) < 0)
            {
                Debug.LogError("[WebShim] No assets are tagged with bundle " + name + " - nothing to simulate");
                return null;
            }
            GameObject anchor = new GameObject("[WebShim] simulated bundle " + name) { hideFlags = HideFlags.HideAndDontSave };
            // The editor's Object keeps its id in m_EntityId; with no native pointer, == null asks whether an object with
            // that id exists.
            FieldInfo entityId = typeof(UnityEngine.Object).GetField("m_EntityId", BindingFlags.Instance | BindingFlags.NonPublic);
            if (entityId != null)
            {
                entityId.SetValue(bundle, entityId.GetValue(anchor));
            }
            s_placeholders[bundle] = name;
            return bundle;
        }

        // Unity 6's editor refuses AssetDatabase calls in a MonoBehaviour constructor or field initializer, and mods
        // load assets there (Expand's ExpandExplodeOnDeath, ExpandWallMimicManager, PortableShip...); a bundle in the
        // player answers them. So the editor indexes every simulated bundle and preloads what constructors ask for
        // when Play Mode starts (EtgWebPlayMode: PlayModePreload.txt + what this class logged as refused before).
        private static readonly Dictionary<string, UnityEngine.Object> s_preloaded = new Dictionary<string, UnityEngine.Object>();
        private static readonly HashSet<string> s_refused = new HashSet<string>();
        private static string s_refusedFile;

        /// <summary>Set by the editor (EtgWebPlayMode): prepares the simulation, see <see cref="StartSimulation"/>.</summary>
        public static Action SimulationStarting;

        // Play Mode with the simulation started. Not Application.isPlaying: constructors may not ask that either.
        private static bool s_playing;

        // Misses already logged this Play Mode. A real bundle's LoadAsset returns null silently, and mods probe several
        // bundles per name (Alexandria's room placeables): ~67,000 warnings in a full smoke test, ~1,800 different.
        private static readonly HashSet<string> s_missingLogged = new HashSet<string>();

        /// <summary>Editor, leaving Play Mode (statics survive it): constructors run by the editor get nothing again.</summary>
        public static void StopSimulation()
        {
            s_playing = false;
        }

        /// <summary>BootLoader, Play Mode: before the game asks for anything (a safe place for AssetDatabase).</summary>
        public static void StartSimulation()
        {
            s_playing = true;
            s_missingLogged.Clear();
            if (SimulationStarting != null)
            {
                SimulationStarting();
            }
        }

        /// <summary>Editor, Play Mode: indexes every bundle; loads a constructor couldn't do are appended to refusedFile.</summary>
        public static void PrepareSimulation(string refusedFile)
        {
            s_refusedFile = refusedFile;
            if (System.IO.File.Exists(refusedFile))
            {
                foreach (string line in System.IO.File.ReadAllLines(refusedFile))
                {
                    s_refused.Add(line);
                }
            }
            foreach (string name in EditorAssets.AllBundleNames())
            {
                EditorAssets.PathsInBundle(name);
                // A placeholder's anchor is a new GameObject, which a constructor can't make either.
                if (!s_bundles.ContainsKey(name))
                {
                    AssetBundle bundle = CreatePlaceholder(name);
                    if ((object)bundle != null)
                    {
                        s_bundles[name] = bundle;
                    }
                }
            }
        }

        /// <summary>Editor, Play Mode: loads an asset now, for a constructor that asks for it later.</summary>
        public static bool Preload(string bundleName, string assetName, Type type)
        {
            UnityEngine.Object asset = SimulatedLoad(Normalize(bundleName), assetName, type);
            if (asset == null)
            {
                return false;
            }
            s_preloaded[PreloadKey(Normalize(bundleName), assetName, type)] = asset;
            return true;
        }

        private static string PreloadKey(string bundleName, string assetName, Type type)
        {
            return bundleName + "\t" + assetName + "\t" + type.FullName + ", " + type.Assembly.GetName().Name;
        }

        private static UnityEngine.Object SimulatedLoad(string bundleName, string assetName, Type type)
        {
            if (!s_playing)
            {
                return null; // an import running a mod component's constructor
            }
            UnityEngine.Object preloaded;
            if (s_preloaded.TryGetValue(PreloadKey(bundleName, assetName, type), out preloaded) && preloaded != null)
            {
                return preloaded;
            }
            try
            {
                return SimulatedLoadNow(bundleName, assetName, type);
            }
            catch (UnityException e) when (e.Message.Contains("MonoBehaviour constructor"))
            {
                string key = PreloadKey(bundleName, assetName, type);
                if (s_refused.Add(key))
                {
                    if (s_refusedFile != null)
                    {
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(s_refusedFile));
                        System.IO.File.AppendAllText(s_refusedFile, key + "\n");
                    }
                    Debug.LogWarning("[WebShim] Play Mode: " + bundleName + " / " + assetName + " (" + type.Name
                        + ") is loaded from a MonoBehaviour constructor, where the editor refuses AssetDatabase; "
                        + "it is preloaded from the next Play Mode on (" + s_refusedFile + ")");
                }
                return null;
            }
        }

        private static UnityEngine.Object SimulatedLoadNow(string bundleName, string assetName, Type type)
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
                        return Array.IndexOf(AllBundles, bundleName) < 0 ? RuntimeCopy(path, asset) : asset;
                    }
                }
            }
            if (s_missingLogged.Add(PreloadKey(bundleName, assetName, type)))
            {
                Debug.LogWarning("[WebShim] Simulated bundle " + bundleName + " has no " + type.Name + " named " + assetName
                    + " (logged once per Play Mode)");
            }
            return null;
        }

        // A mod's bundle prefab, simulated: AssetDatabase hands out the prefab asset itself, which the editor won't let
        // code use like a loaded one (Renderer.material: "Not allowed to access Renderer.material on prefab object") and
        // which mods change (AddComponent...) - changes to the project's assets. A real bundle's prefab is a runtime
        // object: one copy per asset, under a hidden inactive root (no Awake, as for a bundle's prefab). Only for mod
        // bundles: other game assets reference the game's bundle prefabs, which must stay the same objects.
        private static GameObject s_simRoot;
        private static readonly Dictionary<string, GameObject> s_simCopies = new Dictionary<string, GameObject>();

        private static UnityEngine.Object RuntimeCopy(string path, UnityEngine.Object asset)
        {
            GameObject prefab = asset as GameObject;
            Component component = asset as Component;
            if (component != null)
            {
                prefab = component.gameObject;
            }
            if (prefab == null)
            {
                return asset;
            }
            GameObject copy;
            if (!s_simCopies.TryGetValue(path, out copy) || copy == null)
            {
                if (s_simRoot == null)
                {
                    s_simRoot = new GameObject("[WebShim] simulated mod bundle prefabs") { hideFlags = HideFlags.HideAndDontSave };
                    s_simRoot.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(s_simRoot);
                }
                copy = UnityEngine.Object.Instantiate(prefab, s_simRoot.transform);
                copy.name = prefab.name;
                s_simCopies[path] = copy;
            }
            return component != null ? (UnityEngine.Object)copy.GetComponent(component.GetType()) : copy;
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

            // Bundle contents don't change during Play Mode; cached so constructors (PrepareSimulation) don't need the editor.
            private static readonly Dictionary<string, string[]> s_paths = new Dictionary<string, string[]>();
            private static string[] s_allNames;

            public static string[] PathsInBundle(string bundleName)
            {
                string[] paths;
                if (s_paths.TryGetValue(bundleName, out paths))
                {
                    return paths;
                }
                if (s_pathsInBundle == null)
                {
                    s_pathsInBundle = Bind<Func<string, string[]>>("GetAssetPathsFromAssetBundle", typeof(string));
                }
                return s_paths[bundleName] = s_pathsInBundle(bundleName);
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
                if (s_allNames != null)
                {
                    return s_allNames;
                }
                if (s_allBundleNames == null)
                {
                    s_allBundleNames = Bind<Func<string[]>>("GetAllAssetBundleNames");
                }
                return s_allNames = s_allBundleNames();
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

        /// <summary>".../StreamingAssets/Assets/dungeons/base_castle" -> "dungeons/base_castle"; a mod's own
        /// ".../BepInEx/plugins/&lt;package&gt;/expandsharedauto" -> "expandsharedauto" (the web build has it
        /// among its own bundles, mods/README; the file there is an empty placeholder).</summary>
        private static string ToBundleName(string path)
        {
            string p = Normalize(path);
            if (p.Contains("/bepinex/plugins/"))
            {
                return p.Substring(p.LastIndexOf('/') + 1);
            }
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
