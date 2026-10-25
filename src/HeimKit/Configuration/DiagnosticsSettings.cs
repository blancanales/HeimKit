using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Configuration;

namespace HeimKit.Configuration;

public enum DebugVerbosity { Normal, Verbose }

public static class DiagnosticsSettings
{
    private static ConfigEntry<DebugVerbosity>? _verbosity;
    public static DebugVerbosity Verbosity => _verbosity?.Value ?? DebugVerbosity.Normal;
    public static ConfigEntry<bool> Bind(ConfigFile config)
    {
        _verbosity = config.Bind("Diagnostics", "DebugVerbosity", DebugVerbosity.Normal,
            "Normal shows concise diagnostics; Verbose adds detailed evidence when effective feature debugging is enabled. Restart after edits.");
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
