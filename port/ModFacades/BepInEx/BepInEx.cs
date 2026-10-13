using System;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class BepInPlugin : Attribute
    {
        public BepInPlugin(string GUID, string Name, string Version)
        {
            this.GUID = GUID;
            this.Name = Name;
            try
            {
                this.Version = new Version(Version);
            }
            catch
            {
                this.Version = new Version(0, 0);
            }
        }

        public string GUID { get; protected set; }
        public string Name { get; protected set; }
        public Version Version { get; protected set; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class BepInDependency : Attribute
    {
        [Flags]
        public enum DependencyFlags
        {
            HardDependency = 1,
            SoftDependency = 2,
        }

        public BepInDependency(string DependencyGUID, DependencyFlags Flags = DependencyFlags.HardDependency)
        {
            this.DependencyGUID = DependencyGUID;
            this.Flags = Flags;
        }

        public BepInDependency(string DependencyGUID, string MinimumDependencyVersion)
            : this(DependencyGUID, DependencyFlags.HardDependency)
        {
        }

        public string DependencyGUID { get; protected set; }
        public DependencyFlags Flags { get; protected set; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class BepInProcess : Attribute
    {
        public BepInProcess(string ProcessName)
        {
            this.ProcessName = ProcessName;
        }

        public string ProcessName { get; protected set; }
    }

    public class PluginInfo
    {
        public BepInPlugin Metadata { get; internal set; }
        public IEnumerable<BepInDependency> Dependencies { get; internal set; }
        public IEnumerable<BepInProcess> Processes { get; internal set; }
        public string Location { get; internal set; }
        public BaseUnityPlugin Instance { get; internal set; }
        public string TypeName { get; internal set; }

        public override string ToString()
        {
            return Metadata.Name + " " + Metadata.Version;
        }
    }

    public abstract class BaseUnityPlugin : MonoBehaviour
    {
        protected BaseUnityPlugin()
        {
            Info = Chainloader.InfoFor(this);
            Logger = BepInEx.Logging.Logger.CreateLogSource(Info.Metadata.Name);
            Config = new ConfigFile(System.IO.Path.Combine(Paths.ConfigPath, Info.Metadata.GUID + ".cfg"), true);
        }

        public PluginInfo Info { get; private set; }
        protected ManualLogSource Logger { get; private set; }
        public ConfigFile Config { get; private set; }
    }

    /// <summary>
    /// GameRootPath (Resources/ for logs and mod data, BepInEx/config) is persistent - IndexedDB in the
    /// browser, like the saves. PluginPath holds the mods' files, unpacked at boot (WebShim.ModFiles).
    /// </summary>
    public static class Paths
    {
        public static string GameRootPath { get; internal set; }
        public static string BepInExRootPath { get; internal set; }
        public static string ConfigPath { get; internal set; }
        public static string BepInExConfigPath { get; internal set; }
        public static string CachePath { get; internal set; }
        public static string PluginPath { get; internal set; }
        public static string PatcherPluginPath { get; internal set; }
        public static string ManagedPath { get; internal set; }
        public static string ExecutablePath { get; internal set; }
        public static string ProcessName { get { return "EtG"; } }
    }
}
