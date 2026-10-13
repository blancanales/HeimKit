using BepInEx.Configuration;

namespace HeimKit.Configuration;

public static class DiagnosticsSettings
{
    public static ConfigEntry<bool> Bind(ConfigFile config)
    {
        var debugMode = config.Bind("Diagnostics", "DebugMode", false,
            "Enable diagnostics for all features unless their hidden feature override suppresses them. When false, feature DebugMode settings can enable individual diagnostics. Restart after edits.");
        // Consume and remove retired settings so existing files also keep the current global debug switch.
        foreach (string key in new[] { "LogStatus", "StatusIntervalSeconds", "LogRefillDetails", "LogRefillRequests" })
        {
            config.Bind("Diagnostics", key, "");
            config.Remove(new ConfigDefinition("Diagnostics", key));
        }
        return debugMode;
    }
}
