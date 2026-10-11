using BepInEx.Configuration;
using HeimKit.Configuration;

namespace HeimKit.Features.InfiniteTorches;

public sealed class InfiniteTorchesSettings
{
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<float> IntervalSeconds { get; }
    public ConfigEntry<int> MaxMessagesPerPass { get; }
    public ConfigEntry<string> Prefabs { get; }
    public ConfigEntry<bool> DebugMode { get; }

    public InfiniteTorchesSettings(ConfigFile config)
    {
        Enabled = config.Bind("Torches", "Enabled", true, "Automatically refill eligible torches for free. Restart after config edits.");
        IntervalSeconds = config.Bind("Torches", "IntervalSeconds", 5f,
            new ConfigDescription("Seconds between refills. Large bases may take several passes.", new AcceptableValueRange<float>(2f, 60f)));
        MaxMessagesPerPass = config.Bind("Torches", "MaxMessagesPerPass", 32,
            new ConfigDescription("Limits network traffic; processing rotates across candidates.", new AcceptableValueRange<int>(1, 256)));
        Prefabs = config.Bind("Torches", "Prefabs",
            "piece_groundtorch_wood,piece_groundtorch,piece_groundtorch_green,piece_groundtorch_blue,piece_walltorch",
            "Exact prefab allowlist. Only refillable non-toggle lights without wet-flame objects qualify. Fires and braziers are excluded by default.");
        DebugMode = DiagnosticsSettings.Bind(config);
    }
}
