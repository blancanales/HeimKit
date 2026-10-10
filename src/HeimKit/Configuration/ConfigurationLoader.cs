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
            logger.LogInfo(existed
                ? $"Loaded configuration: {path}. Existing settings preserved; missing settings added with defaults."
                : $"Created default configuration: {path}. All feature settings are included.");
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError($"Could not load or create configuration at {path}: {ex.Message}");
            throw;
        }
    }
}
