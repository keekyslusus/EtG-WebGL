using System;
using System.Collections.Generic;

namespace BepInEx.Logging
{
    [Flags]
    public enum LogLevel
    {
        None = 0,
        Fatal = 1,
        Error = 2,
        Warning = 4,
        Message = 8,
        Info = 16,
        Debug = 32,
        All = Fatal | Error | Warning | Message | Info | Debug,
    }

    public interface ILogSource : IDisposable
    {
        string SourceName { get; }
    }

    /// <summary>BepInEx log sources write to the Unity log (the browser console in the web build).</summary>
    public class ManualLogSource : ILogSource
    {
        public ManualLogSource(string sourceName)
        {
            SourceName = sourceName;
        }

        public string SourceName { get; private set; }

        public void Log(LogLevel level, object data)
        {
            string text = "[" + level + ":" + SourceName + "] " + data;
            if ((level & (LogLevel.Fatal | LogLevel.Error)) != 0)
            {
                UnityEngine.Debug.LogError(text);
            }
            else if ((level & LogLevel.Warning) != 0)
            {
                UnityEngine.Debug.LogWarning(text);
            }
            else
            {
                UnityEngine.Debug.Log(text);
            }
        }

        public void LogFatal(object data) { Log(LogLevel.Fatal, data); }
        public void LogError(object data) { Log(LogLevel.Error, data); }
        public void LogWarning(object data) { Log(LogLevel.Warning, data); }
        public void LogMessage(object data) { Log(LogLevel.Message, data); }
        public void LogInfo(object data) { Log(LogLevel.Info, data); }
        public void LogDebug(object data) { Log(LogLevel.Debug, data); }

        public void Dispose()
        {
            Logger.Sources.Remove(this);
        }
    }

    public static class Logger
    {
        private static readonly List<ILogSource> s_sources = new List<ILogSource>();

        public static ICollection<ILogSource> Sources
        {
            get { return s_sources; }
        }

        public static ManualLogSource CreateLogSource(string sourceName)
        {
            var source = new ManualLogSource(sourceName);
            s_sources.Add(source);
            return source;
        }
    }
}
