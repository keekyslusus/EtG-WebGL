using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace BepInEx.Bootstrap
{
    /// <summary>
    /// Creates the plugins of the baked mods: every [BepInPlugin] BaseUnityPlugin in the build, ordered by
    /// [BepInDependency], as components of a DontDestroyOnLoad "BepInEx_Manager" (like BepInEx does).
    /// WebShim.BootLoader calls Start through reflection once the mods' files are unpacked, before the
    /// game's first scene.
    /// </summary>
    public static class Chainloader
    {
        private static readonly Dictionary<string, PluginInfo> s_infos = new Dictionary<string, PluginInfo>();
        private static readonly List<BaseUnityPlugin> s_plugins = new List<BaseUnityPlugin>();
        private static PluginInfo s_constructing;

        public static Dictionary<string, PluginInfo> PluginInfos
        {
            get { return s_infos; }
        }

        public static List<BaseUnityPlugin> Plugins
        {
            get { return s_plugins; }
        }

        public static GameObject ManagerObject { get; private set; }

        /// <param name="pluginPath">Where the mods' plugin folders are (BepInEx/plugins).</param>
        /// <param name="gameRootPath">Writable, persistent stand-in for the game folder.</param>
        public static void Start(string pluginPath, string gameRootPath)
        {
            if (ManagerObject != null)
            {
                return;
            }
            Paths.PluginPath = pluginPath;
            Paths.GameRootPath = gameRootPath;
            Paths.BepInExRootPath = Path.Combine(gameRootPath, "BepInEx");
            Paths.ConfigPath = Path.Combine(Paths.BepInExRootPath, "config");
            Paths.BepInExConfigPath = Path.Combine(Paths.ConfigPath, "BepInEx.cfg");
            Paths.CachePath = Path.Combine(Paths.BepInExRootPath, "cache");
            Paths.PatcherPluginPath = Path.Combine(Paths.BepInExRootPath, "patchers");
            Paths.ManagedPath = Path.Combine(gameRootPath, "EtG_Data/Managed");
            Paths.ExecutablePath = Path.Combine(gameRootPath, "EtG.exe");
            Directory.CreateDirectory(Path.Combine(gameRootPath, "Resources"));
            Directory.CreateDirectory(Paths.ConfigPath);
            // Folders mods write into without creating them. Silver Jacket saves its item stats to
            // <game root>/SilverJacketData/ItemStats.json on every pickup of its items, but only its `slvjckt read_stats`
            // console command creates the folder: without it the pickup throws out of PlayerController's input handling
            // and the player stops responding.
            Directory.CreateDirectory(Path.Combine(gameRootPath, "SilverJacketData"));

            ManagerObject = new GameObject("BepInEx_Manager");
            UnityEngine.Object.DontDestroyOnLoad(ManagerObject);

            var found = new List<KeyValuePair<Type, PluginInfo>>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }
                foreach (Type type in types)
                {
                    if (type.IsAbstract || !typeof(BaseUnityPlugin).IsAssignableFrom(type))
                    {
                        continue;
                    }
                    var meta = (BepInPlugin)type.GetCustomAttributes(typeof(BepInPlugin), false).FirstOrDefault();
                    if (meta == null)
                    {
                        continue;
                    }
                    found.Add(new KeyValuePair<Type, PluginInfo>(type, new PluginInfo
                    {
                        Metadata = meta,
                        Dependencies = type.GetCustomAttributes(typeof(BepInDependency), false).Cast<BepInDependency>().ToList(),
                        Processes = type.GetCustomAttributes(typeof(BepInProcess), false).Cast<BepInProcess>().ToList(),
                        Location = FindLocation(pluginPath, assembly.GetName().Name),
                        TypeName = type.FullName,
                    }));
                }
            }

            foreach (var pair in Sort(found))
            {
                PluginInfo info = pair.Value;
                string missing = info.Dependencies
                    .Where(d => (d.Flags & BepInDependency.DependencyFlags.HardDependency) != 0 && !s_infos.ContainsKey(d.DependencyGUID))
                    .Select(d => d.DependencyGUID).FirstOrDefault();
                if (missing != null)
                {
                    Debug.LogError("[BepInEx] Not loading " + info + ": missing dependency " + missing);
                    continue;
                }
                s_infos[info.Metadata.GUID] = info;
                try
                {
                    s_constructing = info;
                    info.Instance = (BaseUnityPlugin)ManagerObject.AddComponent(pair.Key);
                    s_plugins.Add(info.Instance);
                    Debug.Log("[BepInEx] Loaded " + info + " (" + info.Location + ")");
                }
                catch (Exception e)
                {
                    s_infos.Remove(info.Metadata.GUID);
                    Debug.LogError("[BepInEx] Failed to load " + info + ": " + e);
                }
                finally
                {
                    s_constructing = null;
                }
            }
        }

        // Dependencies first; otherwise the order the assemblies were found in.
        private static List<KeyValuePair<Type, PluginInfo>> Sort(List<KeyValuePair<Type, PluginInfo>> plugins)
        {
            var byGuid = plugins.GroupBy(p => p.Value.Metadata.GUID).ToDictionary(g => g.Key, g => g.First());
            var result = new List<KeyValuePair<Type, PluginInfo>>();
            var state = new Dictionary<string, int>();
            Action<KeyValuePair<Type, PluginInfo>> visit = null;
            visit = p =>
            {
                string guid = p.Value.Metadata.GUID;
                int s;
                if (state.TryGetValue(guid, out s))
                {
                    return; // done, or a cycle (BepInEx refuses those; keep going here)
                }
                state[guid] = 1;
                foreach (BepInDependency dep in p.Value.Dependencies)
                {
                    KeyValuePair<Type, PluginInfo> d;
                    if (byGuid.TryGetValue(dep.DependencyGUID, out d))
                    {
                        visit(d);
                    }
                }
                state[guid] = 2;
                result.Add(p);
            };
            foreach (var p in byGuid.Values)
            {
                visit(p);
            }
            return result;
        }

        // Where the mod's DLL would be: mods/fetch + the web pack keep each package's folder layout
        // (BepInEx/plugins/<package>/...), with an empty placeholder for every DLL.
        private static string FindLocation(string pluginPath, string assemblyName)
        {
            try
            {
                string match = Directory.GetFiles(pluginPath, assemblyName + ".dll", SearchOption.AllDirectories).FirstOrDefault();
                if (match != null)
                {
                    return match;
                }
            }
            catch (Exception)
            {
            }
            return Path.Combine(pluginPath, assemblyName + ".dll");
        }

        internal static PluginInfo InfoFor(BaseUnityPlugin plugin)
        {
            if (s_constructing != null)
            {
                return s_constructing;
            }
            // Created outside the chainloader (e.g. AddComponent by a mod).
            var meta = (BepInPlugin)plugin.GetType().GetCustomAttributes(typeof(BepInPlugin), false).FirstOrDefault()
                ?? new BepInPlugin(plugin.GetType().FullName, plugin.GetType().Name, "0.0");
            PluginInfo info;
            if (s_infos.TryGetValue(meta.GUID, out info))
            {
                return info;
            }
            return new PluginInfo
            {
                Metadata = meta,
                Dependencies = new BepInDependency[0],
                Processes = new BepInProcess[0],
                Location = FindLocation(Paths.PluginPath ?? "", plugin.GetType().Assembly.GetName().Name),
                TypeName = plugin.GetType().FullName,
                Instance = plugin,
            };
        }
    }
}
