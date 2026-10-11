using BepInEx.Configuration;

namespace HeimKit.Configuration;

public static class DiagnosticsSettings
{
    public static ConfigEntry<bool> Bind(ConfigFile config)
    {
        var debugMode = config.Bind("Diagnostics", "DebugMode", false,
            "Enable troubleshooting diagnostics. When false, only standard startup, readiness, warning and error logs are emitted.");
        // Consume and remove retired settings so existing files also show just the one debug switch.
        foreach (string key in new[] { "LogStatus", "StatusIntervalSeconds", "LogRefillDetails", "LogRefillRequests" })
        {
            config.Bind("Diagnostics", key, "");
            config.Remove(new ConfigDefinition("Diagnostics", key));
        }
        return debugMode;
    }
}
