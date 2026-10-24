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

namespace HeimKit.Features.InfiniteTorches;

public sealed class InfiniteTorchesSettings
{
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<float> IntervalSeconds { get; }
    public ConfigEntry<int> MaxMessagesPerPass { get; }
    public ConfigEntry<string> Prefabs { get; }
    public ConfigEntry<bool> IncludeFireplaces { get; }
    public ConfigEntry<bool> Campfires { get; }
    public ConfigEntry<bool> Hearths { get; }
    public ConfigEntry<bool> Bonfires { get; }

    internal int FireplaceSelection => !IncludeFireplaces.Value ? 0
        : (Campfires.Value ? 1 : 0) | (Hearths.Value ? 2 : 0) | (Bonfires.Value ? 4 : 0);

    internal bool FireplaceEnabled(string name) => IncludeFireplaces.Value && (name switch
    {
        "fire_pit" => Campfires.Value,
        "hearth" => Hearths.Value,
        "bonfire" => Bonfires.Value,
        _ => false
    });
    public ConfigEntry<bool> DebugMode { get; }
    public ConfigEntry<bool> GlobalDebugMode { get; }
    public DebugLoggingPolicy DebugLogging { get; }

    public InfiniteTorchesSettings(ConfigFile config, ManualLogSource? logger = null)
    {
        Enabled = config.Bind("Torches", "Enabled", true, "Automatically refill eligible torches, braziers and configured fires for free. Restart after config edits.");
        IntervalSeconds = config.Bind("Torches", "IntervalSeconds", 5f,
            new ConfigDescription("Seconds between refills. Large bases may take several passes.", new AcceptableValueRange<float>(2f, 60f)));
        MaxMessagesPerPass = config.Bind("Torches", "MaxMessagesPerPass", 32,
            new ConfigDescription("Limits network traffic; processing rotates across candidates.", new AcceptableValueRange<int>(1, 256)));
        IncludeFireplaces = config.Bind("Torches", "IncludeFireplaces", false,
            "Enable fireplace refilling, controlled individually by Campfires, Hearths and Bonfires. False excludes these fires even if listed in Prefabs. Native smoke, shelter and water behavior remains active. Restart after edits.");
        Campfires = config.Bind("Torches", "Campfires", true, "Refill campfires when IncludeFireplaces is true. False excludes them even if listed in Prefabs. Restart after edits.");
        Hearths = config.Bind("Torches", "Hearths", true, "Refill hearths when IncludeFireplaces is true. False excludes them even if listed in Prefabs. Restart after edits.");
        Bonfires = config.Bind("Torches", "Bonfires", true, "Refill bonfires when IncludeFireplaces is true. False excludes them even if listed in Prefabs. Restart after edits.");
        Prefabs = config.Bind("Torches", "Prefabs",
            "piece_groundtorch_wood,piece_groundtorch,piece_groundtorch_green,piece_groundtorch_blue,piece_walltorch,piece_brazierceiling01,piece_brazierfloor01,piece_brazierfloor02",
            "Exact prefab allowlist. Only refillable non-toggle lights qualify. Wet-flame objects qualify only for the three default braziers and, with IncludeFireplaces and the corresponding type enabled, campfires, hearths and bonfires.");
        GlobalDebugMode = DiagnosticsSettings.Bind(config);
        DebugMode = config.Bind("Torches", "DebugMode", false, "Enable diagnostics for this feature even when global DebugMode is off. The hidden suppression override still takes precedence. Restart after edits.");
        DebugLogging = DebugLoggingResolver.Resolve(config, "Torches", GlobalDebugMode, logger, DebugMode);
    }
}
