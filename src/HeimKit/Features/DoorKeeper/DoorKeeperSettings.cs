using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;

namespace HeimKit.Features.DoorKeeper;

public sealed class DoorKeeperSettings
{
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<bool> DebugMode { get; }
    public DebugLoggingPolicy DebugLogging { get; }
    public ConfigEntry<float> CloseDelaySeconds { get; }
    public ConfigEntry<float> ClearanceMeters { get; }
    public ConfigEntry<float> ScanRangeMeters { get; }
    public ConfigEntry<float> IntervalSeconds { get; }
    public DoorKeeperSettings(ConfigFile config, ManualLogSource logger)
    {
        Enabled = config.Bind("DoorKeeper", "Enabled", true, "Enable DoorKeeper. Restart after edits.");
        DebugMode = config.Bind("DoorKeeper", "DebugMode", false, "Enable feature diagnostics through the shared debug policy.");
        CloseDelaySeconds = config.Bind("DoorKeeper", "CloseDelaySeconds", 5f, new ConfigDescription("Continuous observed open time before closing. Restart after edits.", new AcceptableValueRange<float>(1f, 300f)));
        ClearanceMeters = config.Bind("DoorKeeper", "ClearanceMeters", 3f, new ConfigDescription("Minimum occupancy sphere radius; native door geometry plus player allowance can enlarge it. Restart after edits.", new AcceptableValueRange<float>(2f, 10f)));
        ScanRangeMeters = config.Bind("DoorKeeper", "ScanRangeMeters", 64f, new ConfigDescription("Range around a rotating ready player. Restart after edits.", new AcceptableValueRange<float>(16f, 128f)));
        IntervalSeconds = config.Bind("DoorKeeper", "IntervalSeconds", 1f, new ConfigDescription("Processing interval. Restart after edits.", new AcceptableValueRange<float>(0.5f, 10f)));
        foreach (float value in new[] { CloseDelaySeconds.Value, ClearanceMeters.Value, ScanRangeMeters.Value, IntervalSeconds.Value })
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new System.ArgumentException("DoorKeeper settings must be finite.");
        DebugLogging = DebugLoggingResolver.Resolve(config, "DoorKeeper", DiagnosticsSettings.Bind(config), logger, DebugMode);
    }
}
