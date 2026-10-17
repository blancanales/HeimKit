using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;

namespace HeimKit.Features.SharedMap;

public sealed class SharedMapSettings
{
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<bool> DebugMode { get; }
    public ConfigEntry<bool> PortalMarkerSync { get; }
    public ConfigEntry<float> PortalClusterDistanceMeters { get; }
    public ConfigEntry<float> TableIntervalSeconds { get; }
    public ConfigEntry<int> MaxTablesPerPass { get; }
    public ConfigEntry<float> SaveIntervalSeconds { get; }
    public ConfigEntry<bool> GlobalDebugMode { get; }
    public DebugLoggingPolicy DebugLogging { get; }

    public SharedMapSettings(ConfigFile config, ManualLogSource? logger = null)
    {
        Enabled = config.Bind("SharedMap", "Enabled", true,
            "Persist community exploration and normalize vanilla table map data, excluding shared death pins. Optionally add player-built portals. Restart after edits.");
        PortalMarkerSync = config.Bind("SharedMap", "PortalMarkerSync", true,
            "Automatically maintain named player-built portal markers in explored terrain. Replace all portal-icon pins from server portal records, blocking stale names and positions. Renamed markers use small persistent offsets to encourage replacement on Read Map; client replacement is not guaranteed. Other icons are preserved except matching portal labels within three metres of a generated marker. Disabling removes generated portal pins while preserving player-owned pins. Restart after edits.");
        PortalClusterDistanceMeters = config.Bind("SharedMap", "PortalClusterDistanceMeters", 60f,
            new ConfigDescription("Group portals within this horizontal distance of a group's first portal into one Multi Portals marker at their centre. Zero shows separate portal markers. Restart after edits.", new AcceptableValueRange<float>(0f, 100f)));
        TableIntervalSeconds = config.Bind("SharedMap", "TableIntervalSeconds", 10f,
            new ConfigDescription("Seconds between nearby table reconciliation passes.", new AcceptableValueRange<float>(2f, 120f)));
        MaxTablesPerPass = config.Bind("SharedMap", "MaxTablesPerPass", 1,
            new ConfigDescription("Maximum table payloads per first-run import tick or periodic reconciliation pass; incoming writes and new tables are handled at their native event.", new AcceptableValueRange<int>(1, 8)));
        SaveIntervalSeconds = config.Bind("SharedMap", "SaveIntervalSeconds", 60f,
            new ConfigDescription("Seconds between dirty community-map saves; lifecycle transitions flush without waiting.", new AcceptableValueRange<float>(10f, 600f)));
        GlobalDebugMode = DiagnosticsSettings.Bind(config);
        DebugMode = config.Bind("SharedMap", "DebugMode", false,
            "Enable this module's diagnostics even when global DebugMode is off. The hidden suppression override still applies. Restart after edits.");
        DebugLogging = DebugLoggingResolver.Resolve(config, "SharedMap", GlobalDebugMode, logger, DebugMode);
    }
}
