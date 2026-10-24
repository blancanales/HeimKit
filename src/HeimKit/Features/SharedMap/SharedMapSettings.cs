using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
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
    public ConfigEntry<float> ResourceMarkerHideDistanceMeters { get; }
    public ConfigEntry<float> SilverDiscoveryRadiusMeters { get; }
    public ConfigEntry<bool> ResourceRemoveDepleted { get; }
    public ConfigEntry<bool> DungeonMarkerSync { get; }
    public ConfigEntry<bool> DungeonDiscoverPreviouslyExplored { get; }
    public ConfigEntry<bool> AllowPlayerMapWrites { get; }
    public ConfigEntry<bool> SharePinCheckmarks { get; }
    public ConfigEntry<bool> DungeonLootProgress { get; }
    public ConfigEntry<bool> RunestoneMarkerSync { get; }
    public ConfigEntry<bool> SpawnerMarkerSync { get; }
    public ConfigEntry<float> TableIntervalSeconds { get; }
    public ConfigEntry<int> MaxTablesPerPass { get; }
    public ConfigEntry<float> SaveIntervalSeconds { get; }
    public ConfigEntry<bool> GlobalDebugMode { get; }
    public DebugLoggingPolicy DebugLogging { get; }

    public SharedMapSettings(ConfigFile config, ManualLogSource? logger = null)
    {
        Enabled = config.Bind("SharedMap", "Enabled", true,
            "Persist community exploration and normalize vanilla table map data, excluding shared death pins. Optionally add player-built portals, outdoor resource deposits and supported dungeon entrances. Restart after edits.");
        PortalMarkerSync = config.Bind("SharedMap", "PortalMarkerSync", true,
            "Automatically maintain named player-built portal markers in explored terrain. Replace all portal-icon pins from server portal records, blocking stale names and positions. Renamed markers use small persistent offsets to encourage replacement on Read Map; client replacement is not guaranteed. Player hammer pins are preserved. Other icons are preserved except matching portal labels within three metres of a generated marker. Disabling removes generated portal pins while preserving player-owned pins. Restart after edits.");
        PortalClusterDistanceMeters = config.Bind("SharedMap", "PortalClusterDistanceMeters", 60f,
            new ConfigDescription("Group portals within this horizontal distance of a group's first portal into one Multi Portals marker at their centre. Zero shows separate portal markers. Restart after edits.", new AcceptableValueRange<float>(0f, 100f)));
        ResourceMarkerSync = config.Bind("SharedMap", "ResourceMarkerSync", true,
            "Record outdoor Copper, Tin, Silver, Obsidian and static Flametal deposits and share dot markers through Read Map. Excludes scraps, dungeons, creatures and ordinary stone. Independent of PortalMarkerSync. Restart after edits.");
        ResourceDiscoverPreviouslyExplored = config.Bind("SharedMap", "ResourceDiscoverPreviouslyExplored", false,
            "Also discover remaining eligible deposits in previously community-explored terrain without a player revisit. False collects only within current player exploration range. Silver always requires mining discovery evidence. Previously recorded discoveries persist either way. Restart after edits.");
        ResourceClusterDistanceMeters = config.Bind("SharedMap", "ResourceClusterDistanceMeters", 30f,
            new ConfigDescription("Group same-type discoveries within this horizontal distance of a fixed first discovery. Tin groups show remaining discovered node counts; zero disables grouping. Restart after edits.", new AcceptableValueRange<float>(0f, 100f)));
        ResourceMarkerHideDistanceMeters = config.Bind("SharedMap", "ResourceMarkerHideDistanceMeters", 10f,
            new ConfigDescription("Hide generated resource markers at or within this horizontal distance of published portal, dungeon, runestone or physical-spawner markers. Compare final publication positions; discoveries and depletion tracking are retained. Personal pins and resource-to-resource grouping are unchanged. Zero disables hiding. Restart after edits.", new AcceptableValueRange<float>(0f, 100f)));
        SilverDiscoveryRadiusMeters = config.Bind("SharedMap", "SilverDiscoveryRadiusMeters", 10f,
            new ConfigDescription("Reveal only the nearest undiscovered Silver vein within this horizontal distance of an observed player pickaxe impact. A direct pickaxe hit always selects the struck eligible Silver vein. Old explored terrain cannot bypass mining discovery. Restart after edits.", new AcceptableValueRange<float>(1f, 30f)));
        ResourceRemoveDepleted = config.Bind("SharedMap", "ResourceRemoveDepleted", true,
            "Remove resource nodes only after confirmed terminal destruction or complete depleted health. Missing/unloaded records never establish depletion. False retains historical nodes in markers. Restart after edits.");
        DungeonMarkerSync = config.Bind("SharedMap", "DungeonMarkerSync", true,
            "Record supported dungeon exterior entrances within native player exploration range and share labeled hammer markers through Read Map. Publish one dungeon anchor within ten horizontal metres and clean nearby uploaded dungeon-labeled hammer copies. Retain independent saved discoveries. Independent of portal and resource markers. Disabling retains discoveries and removes generated dungeon pins during normalization. Restart after edits.");
        DungeonLootProgress = config.Bind("SharedMap", "DungeonLootProgress", true,
            "Show remaining permanent-pickup percentages after observed entry or verified already-picked items. Supported empty inventories show 0%. Visits persist across restarts. Restart after edits.");
        SharePinCheckmarks = config.Bind("SharedMap", "SharePinCheckmarks", false,
            "Share player X checkmarks through accepted Write Map uploads and Read Map. Requires AllowPlayerMapWrites. False leaves player checkmarks personal. Manual checkmarks do not change dungeon pickup percentages. Restart after edits.");
        DungeonDiscoverPreviouslyExplored = config.Bind("SharedMap", "DungeonDiscoverPreviouslyExplored", false,
            "Also discover supported placed dungeon entrances in previously community-explored terrain without a revisit. Never reveals entrances in unexplored terrain or generates locations. Existing discoveries persist either way. Restart after edits.");
        AllowPlayerMapWrites = config.Bind("SharedMap", "AllowPlayerMapWrites", true,
            "Accept player cartography uploads, including exploration and ordinary pins. False rejects incoming map-field changes before importing terrain, retaining server data and Read Map. Vanilla clients still show Write Map and may report success locally. Existing table data and first-run import are retained. Restart after edits.");
        RunestoneMarkerSync = config.Bind("SharedMap", "RunestoneMarkerSync", true,
            "Record validated placed lore runestones within native exploration range or saved community exploration and share dot markers. Disabling removes generated table pins and retains discovery history. Independent of other marker switches. Restart after edits.");
        SpawnerMarkerSync = config.Bind("SharedMap", "SpawnerMarkerSync", true,
            "Record visible, player-destructible persistent recurring spawner objects within native exploration range or saved community exploration and share dot markers. Hide confirmed destroyed sources. Excludes invisible spawn points, one-shot sources and unassociated interiors. Independent of other marker switches. Restart after edits.");
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
