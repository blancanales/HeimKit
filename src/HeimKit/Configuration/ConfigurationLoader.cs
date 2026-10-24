using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace HeimKit.Configuration;

public static class ConfigurationLoader
{
    // Bind all feature settings before saving so a first-use file contains every default.
    public static T Load<T>(string configDirectory, ManualLogSource logger, Func<ConfigFile, T> bindSettings)
    {
        string path = Path.Combine(configDirectory, "heimkit.cfg");
        bool existed = File.Exists(path);
        try
        {
            var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
            T result = bindSettings(config);
            config.Save();
            ConfigurationLayout.Apply(path);
            config.SaveOnConfigSet = true;
            // BepInEx saves before raising SettingChanged; retain layout after automatic saves too.
            config.SettingChanged += (_, __) =>
            {
                if (!config.SaveOnConfigSet) return;
                try { ConfigurationLayout.Apply(path); }
                catch (Exception error)
                {
                    DiagnosticLog.Write(logger, "HeimKit", CoreLogTopics.Config, "Could not organize saved configuration sections.", level: LogLevel.Error, error: error);
                }
            };
            DiagnosticLog.Write(logger, "HeimKit", existed
                ? $"Loaded configuration: {path}."
                : $"Created default configuration: {path}.");
            return result;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(logger, "HeimKit", CoreLogTopics.Config, $"Could not load or create configuration at {path}.", level: LogLevel.Error, error: ex);
            throw;
        }
    }
}
