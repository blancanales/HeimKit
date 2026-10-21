using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;

namespace HeimKit.Features.ForestRenewal;

public sealed class ForestRenewalSettings
{
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<bool> DebugMode { get; }
    public DebugLoggingPolicy DebugLogging { get; }
    public ConfigEntry<float> DelaySeconds { get; }
    public ConfigEntry<int> ChancePercent { get; }
    public ConfigEntry<float> StructureExclusionMeters { get; }
    public ConfigEntry<float> PlantExclusionMeters { get; }
    public ConfigEntry<bool> ExcludeClearedGround { get; }
    public ConfigEntry<float> ExclusionRadiusMeters { get; }
    public ConfigEntry<float> LocationCooldownHours { get; }
    public ConfigEntry<int> MaxPending { get; }
    public ConfigEntry<float> ScanRangeMeters { get; }
    public ConfigEntry<float> IntervalSeconds { get; }
    public ForestRenewalSettings(ConfigFile config, ManualLogSource logger)
    {
        Enabled = config.Bind("ForestRenewal", "Enabled", true, "Enable ForestRenewal. Restart after edits.");
        DebugMode = config.Bind("ForestRenewal", "DebugMode", false, "Enable feature diagnostics through the shared debug policy.");
        DelaySeconds = config.Bind("ForestRenewal", "DelaySeconds", 300f, new ConfigDescription("Delay from first stump observation or confirmed removal. Restart after edits.", new AcceptableValueRange<float>(10f, 86400f)));
        ChancePercent = config.Bind("ForestRenewal", "ChancePercent", 50, new ConfigDescription("One renewal chance per original stump. Restart after edits.", new AcceptableValueRange<int>(0, 100)));
        StructureExclusionMeters = config.Bind("ForestRenewal", "StructureExclusionMeters", 10f, new ConfigDescription("Horizontal clearance from player-built collision footprints; zero disables this exclusion. Restart after edits.", new AcceptableValueRange<float>(0f, 50f)));
        PlantExclusionMeters = config.Bind("ForestRenewal", "PlantExclusionMeters", 10f, new ConfigDescription("Horizontal clearance from existing saplings and crops. Native growth spacing remains a minimum. Restart after edits.", new AcceptableValueRange<float>(10f, 50f)));
        ExcludeClearedGround = config.Bind("ForestRenewal", "ExcludeClearedGround", true, "Exclude dirt, cultivated and paved ground. Restart after edits.");
        ExclusionRadiusMeters = config.Bind("ForestRenewal", "ExclusionRadiusMeters", 3f, new ConfigDescription("Terrain paint exclusion radius. Restart after edits.", new AcceptableValueRange<float>(0f, 10f)));
        LocationCooldownHours = config.Bind("ForestRenewal", "LocationCooldownHours", 24f, new ConfigDescription("Native world hours between renewals within five metres. Restart after edits.", new AcceptableValueRange<float>(1f, 720f)));
        MaxPending = config.Bind("ForestRenewal", "MaxPending", 1024, new ConfigDescription("Maximum pending renewals. Restart after edits.", new AcceptableValueRange<int>(1, 4096)));
        ScanRangeMeters = config.Bind("ForestRenewal", "ScanRangeMeters", 64f, new ConfigDescription("Processing range around a rotating ready player. Restart after edits.", new AcceptableValueRange<float>(16f, 128f)));
        IntervalSeconds = config.Bind("ForestRenewal", "IntervalSeconds", 2f, new ConfigDescription("Processing interval. Restart after edits.", new AcceptableValueRange<float>(1f, 30f)));
        foreach (float value in new[] { DelaySeconds.Value, StructureExclusionMeters.Value, PlantExclusionMeters.Value, ExclusionRadiusMeters.Value, LocationCooldownHours.Value, ScanRangeMeters.Value, IntervalSeconds.Value })
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new System.ArgumentException("ForestRenewal settings must be finite.");
        DebugLogging = DebugLoggingResolver.Resolve(config, "ForestRenewal", DiagnosticsSettings.Bind(config), logger, DebugMode);
    }
}
