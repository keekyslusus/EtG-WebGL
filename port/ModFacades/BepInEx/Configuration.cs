using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BepInEx.Configuration
{
    public class ConfigDefinition
    {
        public ConfigDefinition(string section, string key)
        {
            Section = section;
            Key = key;
        }

        public string Section { get; private set; }
        public string Key { get; private set; }
    }

    public class ConfigDescription
    {
        public ConfigDescription(string description, object acceptableValues = null, params object[] tags)
        {
            Description = description ?? "";
        }

        public string Description { get; private set; }
    }

    public abstract class ConfigEntryBase
    {
        protected ConfigEntryBase(ConfigFile file, ConfigDefinition definition, Type settingType, object defaultValue, ConfigDescription description)
        {
            ConfigFile = file;
            Definition = definition;
            SettingType = settingType;
            DefaultValue = defaultValue;
            Description = description ?? new ConfigDescription("");
        }

        public ConfigFile ConfigFile { get; private set; }
        public ConfigDefinition Definition { get; private set; }
        public Type SettingType { get; private set; }
        public object DefaultValue { get; private set; }
        public ConfigDescription Description { get; private set; }
        public abstract object BoxedValue { get; set; }

        public string GetSerializedValue()
        {
            return ConfigFile.Serialize(BoxedValue);
        }

        public void SetSerializedValue(string value)
        {
            object parsed;
            if (ConfigFile.TryDeserialize(value, SettingType, out parsed))
            {
                BoxedValue = parsed;
            }
        }
    }

    public sealed class ConfigEntry<T> : ConfigEntryBase
    {
        private T m_value;

        internal ConfigEntry(ConfigFile file, ConfigDefinition definition, T defaultValue, ConfigDescription description)
            : base(file, definition, typeof(T), defaultValue, description)
        {
            m_value = defaultValue;
        }

        public event EventHandler SettingChanged;

        public T Value
        {
            get { return m_value; }
            set
            {
                if (Equals(m_value, value))
                {
                    return;
                }
                m_value = value;
                ConfigFile.OnChanged();
                EventHandler handler = SettingChanged;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
        }

        public override object BoxedValue
        {
            get { return Value; }
            set { Value = (T)value; }
        }
    }

    /// <summary>
    /// BepInEx's .cfg format (sections, "Key = Value", '#' comments), enough to keep the mods'
    /// settings between sessions. Values of keys no plugin has bound yet are kept as text.
    /// </summary>
    public class ConfigFile
    {
        private readonly Dictionary<string, ConfigEntryBase> m_entries = new Dictionary<string, ConfigEntryBase>();
        private readonly Dictionary<string, string> m_orphans = new Dictionary<string, string>();

        public ConfigFile(string configPath, bool saveOnInit)
        {
            ConfigFilePath = configPath;
            SaveOnConfigSet = true;
            Reload();
            if (saveOnInit)
            {
                Save();
            }
        }

        public string ConfigFilePath { get; private set; }
        public bool SaveOnConfigSet { get; set; }

        private static string KeyOf(string section, string key)
        {
            return section + "\n" + key;
        }

        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description)
        {
            return Bind(new ConfigDefinition(section, key), defaultValue, new ConfigDescription(description));
        }

        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, ConfigDescription configDescription = null)
        {
            return Bind(new ConfigDefinition(section, key), defaultValue, configDescription);
        }

        public ConfigEntry<T> Bind<T>(ConfigDefinition configDefinition, T defaultValue, ConfigDescription configDescription = null)
        {
            string k = KeyOf(configDefinition.Section, configDefinition.Key);
            ConfigEntryBase existing;
            if (m_entries.TryGetValue(k, out existing))
            {
                return (ConfigEntry<T>)existing;
            }
            var entry = new ConfigEntry<T>(this, configDefinition, defaultValue, configDescription);
            string text;
            if (m_orphans.TryGetValue(k, out text))
            {
                entry.SetSerializedValue(text);
                m_orphans.Remove(k);
            }
            m_entries[k] = entry;
            if (SaveOnConfigSet)
            {
                Save();
            }
            return entry;
        }

        internal void OnChanged()
        {
            if (SaveOnConfigSet)
            {
                Save();
            }
        }

        public void Reload()
        {
            if (!File.Exists(ConfigFilePath))
            {
                return;
            }
            string section = "";
            foreach (string raw in File.ReadAllLines(ConfigFilePath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2);
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                ConfigEntryBase entry;
                if (m_entries.TryGetValue(KeyOf(section, key), out entry))
                {
                    entry.SetSerializedValue(value);
                }
                else
                {
                    m_orphans[KeyOf(section, key)] = value;
                }
            }
        }

        public void Save()
        {
            var sb = new StringBuilder();
            var all = m_entries.Values.Select(e => new { e.Definition.Section, e.Definition.Key, Value = e.GetSerializedValue(), Description = e.Description.Description })
                .Concat(m_orphans.Select(o => new { Section = o.Key.Split('\n')[0], Key = o.Key.Split('\n')[1], Value = o.Value, Description = "" }));
            foreach (var group in all.GroupBy(e => e.Section).OrderBy(g => g.Key))
            {
                sb.Append('[').Append(group.Key).Append("]\n\n");
                foreach (var e in group)
                {
                    if (e.Description.Length > 0)
                    {
                        sb.Append("## ").Append(e.Description.Replace("\n", "\n## ")).Append('\n');
                    }
                    sb.Append(e.Key).Append(" = ").Append(e.Value).Append("\n\n");
                }
            }
            try
            {
                string dir = Path.GetDirectoryName(ConfigFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                WebShim.FileShim.WriteAllText(ConfigFilePath, sb.ToString());
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[BepInEx] Can't save " + ConfigFilePath + ": " + e.Message);
            }
        }

        internal static string Serialize(object value)
        {
            if (value == null)
            {
                return "";
            }
            if (value is IFormattable)
            {
                return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
            }
            return value.ToString();
        }

        internal static bool TryDeserialize(string text, Type type, out object value)
        {
            value = null;
            try
            {
                if (type == typeof(string))
                {
                    value = text;
                }
                else if (type.IsEnum)
                {
                    value = Enum.Parse(type, text, true);
                }
                else
                {
                    value = Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
