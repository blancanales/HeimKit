using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.WeatherGuard.Models;

namespace HeimKit.Features.WeatherGuard;

public sealed class WeatherGuardSettings
{
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<bool> ProtectRain { get; }
    public ConfigEntry<bool> ProtectUnderwater { get; }
    public ConfigEntry<string> ExcludedPrefabs { get; }
    public ConfigEntry<bool> DebugMode { get; }
    public ConfigEntry<bool> GlobalDebugMode { get; }
    public DebugLoggingPolicy DebugLogging { get; }
    public ConfigEntry<DebugLogLevel> DebugLogLevel { get; }
    public ConfigEntry<bool> LogWorldCreatedItems { get; }

    public WeatherGuardSettings(ConfigFile config, ManualLogSource? logger = null)
    {
        Enabled = config.Bind("WeatherGuard", "Enabled", false,
            "Enable automatic rain/water-wear protection for player-built structures using server processing. Preserve creators. World-created repair claims are inactive. Restart after edits.");
        ProtectRain = config.Bind("WeatherGuard", "ProtectRain", true, "Prevent rain wear on eligible structures. Restart after edits.");
        ProtectUnderwater = config.Bind("WeatherGuard", "ProtectUnderwater", true, "Prevent underwater wetness wear on eligible structures. Restart after edits.");
        ExcludedPrefabs = config.Bind("WeatherGuard", "ExcludedPrefabs", "", "Comma-separated exact case-sensitive prefab identifiers to leave under native processing; no wildcards. Restart after edits.");
        GlobalDebugMode = DiagnosticsSettings.Bind(config);
        DebugMode = config.Bind("WeatherGuard", "DebugMode", false, "Enable diagnostics for this feature even when global DebugMode is off. The hidden suppression override still takes precedence. Restart after edits.");
        DebugLogging = DebugLoggingResolver.Resolve(config, "WeatherGuard", GlobalDebugMode, logger, DebugMode);
        DebugLogLevel = config.Bind("WeatherGuard", "DebugLogLevel", Models.DebugLogLevel.Medium,
            new ConfigDescription("DebugMode output: Medium reports qualifying item protection status; Detailed retains wear, health, loading and ownership evidence. Restart after edits.",
                new WeatherGuardDebugLogLevelValues()));
        LogWorldCreatedItems = config.Bind("WeatherGuard", "LogWorldCreatedItems", false,
            "Include otherwise qualifying world-created structures in debug logs. Logging only; these structures remain unprotected. Restart after edits.");
    }
}
