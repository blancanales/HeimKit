using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;

namespace HeimKit.Features.SharedMap;

public sealed class SharedMapSettings
{
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<bool> DebugMode { get; }
    public ConfigEntry<string> PinDebugNameFilter { get; }
    public ConfigEntry<bool> PortalMarkerSync { get; }
    public ConfigEntry<float> PortalClusterDistanceMeters { get; }
    public ConfigEntry<bool> ResourceMarkerSync { get; }
    public ConfigEntry<bool> ResourceDiscoverPreviouslyExplored { get; }
    public ConfigEntry<float> ResourceClusterDistanceMeters { get; }
    public ConfigEntry<float> SilverDiscoveryRadiusMeters { get; }
    public ConfigEntry<bool> ResourceRemoveDepleted { get; }
    public ConfigEntry<float> TableIntervalSeconds { get; }
    public ConfigEntry<int> MaxTablesPerPass { get; }
    public ConfigEntry<float> SaveIntervalSeconds { get; }
    public ConfigEntry<bool> GlobalDebugMode { get; }
    public DebugLoggingPolicy DebugLogging { get; }

    public SharedMapSettings(ConfigFile config, ManualLogSource? logger = null)
    {
        Enabled = config.Bind("SharedMap", "Enabled", true,
            "Persist community exploration and normalize vanilla table map data, excluding shared death pins. Optionally add player-built portals and outdoor resource deposits. Restart after edits.");
        PortalMarkerSync = config.Bind("SharedMap", "PortalMarkerSync", true,
            "Automatically maintain named player-built portal markers in explored terrain. Replace all portal-icon pins from server portal records, blocking stale names and positions. Renamed markers use small persistent offsets to encourage replacement on Read Map; client replacement is not guaranteed. Other icons are preserved except matching portal labels within three metres of a generated marker. Disabling removes generated portal pins while preserving player-owned pins. Restart after edits.");
        PortalClusterDistanceMeters = config.Bind("SharedMap", "PortalClusterDistanceMeters", 60f,
            new ConfigDescription("Group portals within this horizontal distance of a group's first portal into one Multi Portals marker at their centre. Zero shows separate portal markers. Restart after edits.", new AcceptableValueRange<float>(0f, 100f)));
        ResourceMarkerSync = config.Bind("SharedMap", "ResourceMarkerSync", true,
            "Record outdoor Copper, Tin, Silver, Obsidian and static Flametal deposits and share dot markers through Read Map. Excludes scraps, dungeons, creatures and ordinary stone. Independent of PortalMarkerSync. Restart after edits.");
        ResourceDiscoverPreviouslyExplored = config.Bind("SharedMap", "ResourceDiscoverPreviouslyExplored", false,
            "Also discover remaining eligible deposits in previously community-explored terrain without a player revisit. False collects only within current player exploration range. Silver always requires mining discovery evidence. Previously recorded discoveries persist either way. Restart after edits.");
        ResourceClusterDistanceMeters = config.Bind("SharedMap", "ResourceClusterDistanceMeters", 30f,
            new ConfigDescription("Group same-type discoveries within this horizontal distance of a fixed first discovery. Tin groups show remaining discovered node counts; zero disables grouping. Restart after edits.", new AcceptableValueRange<float>(0f, 100f)));
        SilverDiscoveryRadiusMeters = config.Bind("SharedMap", "SilverDiscoveryRadiusMeters", 10f,
            new ConfigDescription("Reveal only the nearest undiscovered Silver vein within this horizontal distance of an observed player pickaxe impact. A direct pickaxe hit always selects the struck eligible Silver vein. Old explored terrain cannot bypass mining discovery. Restart after edits.", new AcceptableValueRange<float>(1f, 30f)));
        ResourceRemoveDepleted = config.Bind("SharedMap", "ResourceRemoveDepleted", true,
            "Remove resource nodes only after confirmed terminal destruction or complete depleted health. Missing/unloaded records never establish depletion. False retains historical nodes in markers. Restart after edits.");
        TableIntervalSeconds = config.Bind("SharedMap", "TableIntervalSeconds", 10f,
            new ConfigDescription("Seconds between nearby table reconciliation passes.", new AcceptableValueRange<float>(2f, 120f)));
        MaxTablesPerPass = config.Bind("SharedMap", "MaxTablesPerPass", 1,
            new ConfigDescription("Maximum table payloads per first-run import tick, periodic reconciliation pass or requested pin-administration tick; incoming writes and new tables are handled at their native event.", new AcceptableValueRange<int>(1, 8)));
        SaveIntervalSeconds = config.Bind("SharedMap", "SaveIntervalSeconds", 60f,
            new ConfigDescription("Seconds between dirty community-map saves; lifecycle transitions flush without waiting.", new AcceptableValueRange<float>(10f, 600f)));
        PinDebugNameFilter = config.Bind("SharedMap", "PinDebugNameFilter", "",
            "Limit per-pin Debug detail to this exact case-sensitive label, or leave empty to sample all labels. Summary counts cover all labels. Requires effective Debug logging; does not affect pins or normal output. Restart after edits.");
        GlobalDebugMode = DiagnosticsSettings.Bind(config);
        DebugMode = config.Bind("SharedMap", "DebugMode", false,
            "Enable this module's diagnostics even when global DebugMode is off. The hidden suppression override still applies. Restart after edits.");
        DebugLogging = DebugLoggingResolver.Resolve(config, "SharedMap", GlobalDebugMode, logger, DebugMode);
    }
}
