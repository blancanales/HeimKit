using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;

namespace HeimKit.Features.SeaSalvage;

public sealed class SeaSalvageSettings
{
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<bool> DebugMode { get; }
    public DebugLoggingPolicy DebugLogging { get; }
    public ConfigEntry<float> MinimumDepthMeters { get; }
    public ConfigEntry<float> MaximumDepthMeters { get; }
    public ConfigEntry<float> DelaySeconds { get; }
    public ConfigEntry<float> ScanRangeMeters { get; }
    public ConfigEntry<float> GroupRadiusMeters { get; }
    public ConfigEntry<int> SlotsPerCrate { get; }
    public ConfigEntry<int> MaxItemsPerPass { get; }
    public ConfigEntry<float> IntervalSeconds { get; }
    public SeaSalvageSettings(ConfigFile config, ManualLogSource logger)
    {
        Enabled = config.Bind("SeaSalvage", "Enabled", true, "Enable SeaSalvage. Restart after edits.");
        DebugMode = config.Bind("SeaSalvage", "DebugMode", false, "Enable feature diagnostics through the shared debug policy.");
        MinimumDepthMeters = config.Bind("SeaSalvage", "MinimumDepthMeters", 4f, new ConfigDescription("Minimum item depth beneath native water. Restart after edits.", new AcceptableValueRange<float>(3f, 100f)));
        MaximumDepthMeters = config.Bind("SeaSalvage", "MaximumDepthMeters", 50f, new ConfigDescription("Maximum salvage depth. Restart after edits.", new AcceptableValueRange<float>(4f, 500f)));
        DelaySeconds = config.Bind("SeaSalvage", "DelaySeconds", 30f, new ConfigDescription("Continuous unchanged source settling interval. Restart after edits.", new AcceptableValueRange<float>(10f, 3600f)));
        ScanRangeMeters = config.Bind("SeaSalvage", "ScanRangeMeters", 128f, new ConfigDescription("Range around a rotating ready player. Restart after edits.", new AcceptableValueRange<float>(32f, 512f)));
        GroupRadiusMeters = config.Bind("SeaSalvage", "GroupRadiusMeters", 4f, new ConfigDescription("Maximum horizontal distance from first grouped drop; zero means one per crate. Restart after edits.", new AcceptableValueRange<float>(0f, 10f)));
        SlotsPerCrate = config.Bind("SeaSalvage", "SlotsPerCrate", 4, new ConfigDescription("Whole drops per native four-slot crate. Restart after edits.", new AcceptableValueRange<int>(1, 4)));
        MaxItemsPerPass = config.Bind("SeaSalvage", "MaxItemsPerPass", 16, new ConfigDescription("Maximum whole item drops transferred per pass. Restart after edits.", new AcceptableValueRange<int>(1, 32)));
        IntervalSeconds = config.Bind("SeaSalvage", "IntervalSeconds", 2f, new ConfigDescription("Processing interval. Restart after edits.", new AcceptableValueRange<float>(1f, 30f)));
        foreach (float value in new[] { MinimumDepthMeters.Value, MaximumDepthMeters.Value, DelaySeconds.Value, ScanRangeMeters.Value, GroupRadiusMeters.Value, IntervalSeconds.Value })
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new System.ArgumentException("SeaSalvage settings must be finite.");
        DebugLogging = DebugLoggingResolver.Resolve(config, "SeaSalvage", DiagnosticsSettings.Bind(config), logger, DebugMode);
    }
}
