using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace HeimKit.Configuration;

public static class DebugLoggingResolver
{
    public const string Key = "DisableDebugLogging";
    private sealed class OverrideState
    {
        public bool Suppressed;
        public bool Invalid;
        public bool Warned;
    }
    private static readonly ConditionalWeakTable<ConfigFile, Dictionary<string, OverrideState>> Cache = new();
    public static DebugLoggingPolicy Resolve(ConfigFile config, string section, ConfigEntry<bool> global, ManualLogSource? logger = null, ConfigEntry<bool>? feature = null)
    {
        var states = Cache.GetOrCreateValue(config);
        lock (states)
        {
            if (!states.TryGetValue(section, out var state))
            {
                state = new OverrideState();
                try
                {
                    string? value = Read(config, section);
                    if (value != null)
                    {
                        if (bool.TryParse(value, out bool suppressed)) state.Suppressed = suppressed;
                        else { state.Suppressed = true; state.Invalid = true; }
                    }
                }
                catch (IOException) { state.Suppressed = true; state.Invalid = true; }
                catch (UnauthorizedAccessException) { state.Suppressed = true; state.Invalid = true; }
                states.Add(section, state);
            }
            if (state.Invalid && !state.Warned && logger != null)
            {
                state.Warned = true;
                DiagnosticLog.Write(logger, "HeimKit", "Invalid or unreadable feature debug override; feature diagnostics suppressed until corrected and restarted", level: LogLevel.Warning);
            }
            return new DebugLoggingPolicy(global, state.Suppressed, feature);
        }
    }
    private static string? Read(ConfigFile config, string section)
    {
        var definition = new ConfigDefinition(section, Key);
        if (config.ContainsKey(definition)) return config[definition].GetSerializedValue();
        if (!File.Exists(config.ConfigFilePath)) return null;
        // Match ConfigFile.Reload's section/key parsing and last-value-wins behavior.
        // Leave the entry unbound so BepInEx preserves manual values without generating defaults.
        string currentSection = "";
        string? value = null;
        foreach (string line in File.ReadLines(config.ConfigFilePath))
        {
            string text = line.Trim();
            if (text.StartsWith("#")) continue;
            if (text.StartsWith("[") && text.EndsWith("]"))
            { currentSection = text.Substring(1, text.Length - 2); continue; }
            int equals = text.IndexOf('=');
            if (equals >= 0 && currentSection == section && text.Substring(0, equals).Trim() == Key)
                value = text.Substring(equals + 1).Trim();
        }
        return value;
    }
}
