using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;

namespace HeimKit.Features.InfiniteTorches;

public sealed class InfiniteTorchesSettings
{
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<float> IntervalSeconds { get; }
    public ConfigEntry<int> MaxMessagesPerPass { get; }
    public ConfigEntry<string> Prefabs { get; }
    public ConfigEntry<bool> DebugMode { get; }
    public ConfigEntry<bool> GlobalDebugMode { get; }
    public DebugLoggingPolicy DebugLogging { get; }

    public InfiniteTorchesSettings(ConfigFile config, ManualLogSource? logger = null)
    {
        Enabled = config.Bind("Torches", "Enabled", true, "Automatically refill eligible torches and braziers for free. Restart after config edits.");
        IntervalSeconds = config.Bind("Torches", "IntervalSeconds", 5f,
            new ConfigDescription("Seconds between refills. Large bases may take several passes.", new AcceptableValueRange<float>(2f, 60f)));
        MaxMessagesPerPass = config.Bind("Torches", "MaxMessagesPerPass", 32,
            new ConfigDescription("Limits network traffic; processing rotates across candidates.", new AcceptableValueRange<int>(1, 256)));
        Prefabs = config.Bind("Torches", "Prefabs",
            "piece_groundtorch_wood,piece_groundtorch,piece_groundtorch_green,piece_groundtorch_blue,piece_walltorch,piece_brazierceiling01,piece_brazierfloor01,piece_brazierfloor02",
            "Exact prefab allowlist. Only refillable non-toggle lights qualify. Wet-flame objects are supported for the three default brazier prefabs only. Campfires and hearths are excluded by default.");
        GlobalDebugMode = DiagnosticsSettings.Bind(config);
        DebugMode = config.Bind("Torches", "DebugMode", false, "Enable diagnostics for this feature even when global DebugMode is off. The hidden suppression override still takes precedence. Restart after edits.");
        DebugLogging = DebugLoggingResolver.Resolve(config, "Torches", GlobalDebugMode, logger, DebugMode);
    }
}
