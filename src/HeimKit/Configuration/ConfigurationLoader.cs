using HeimKit.Shared;
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
            config.SaveOnConfigSet = true;
            DiagnosticLog.Write(logger, "HeimKit", DiagnosticEvent.Lifecycle, existed
                ? $"Loaded configuration: {path}."
                : $"Created default configuration: {path}.");
            return result;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(logger, "HeimKit", DiagnosticEvent.Lifecycle, $"Could not load or create configuration at {path}: {ex.Message}", level: LogLevel.Error);
            throw;
        }
    }
}
