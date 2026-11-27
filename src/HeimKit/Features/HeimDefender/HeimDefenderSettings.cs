using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;

namespace HeimKit.Features.HeimDefender;
internal sealed class HeimDefenderSettings
{
    internal readonly ConfigEntry<bool> Enabled, FreeWoodBolts, DebugMode;
    internal readonly ConfigEntry<int> BoltsPerReload, BoltType;
    internal static readonly string[] BoltLabels = { "Wooden", "Black Metal", "Flametal", "Bloodgold" };
    private static readonly string[] BoltPrefabs = { HeimDefenderBallistaPolicy.Wood, "TurretBolt", "TurretBoltFlametal", "TurretBoltBloodgold" };
    internal string SelectedAmmo => BoltPrefabs[BoltType.Value];
    internal readonly DebugLoggingPolicy Debug;
    internal readonly string Directory;
    internal HeimDefenderSettings(ConfigFile file, ManualLogSource log)
    {
        Enabled = file.Bind("HeimDefender", "Enabled", true, "Prevent player-built Ballistas from selecting players or tames.");
        FreeWoodBolts = file.Bind("HeimDefender", "FreeWoodBolts", false, "Periodically top up the magazine to its configured stock with the selected bolts.");
        BoltType = file.Bind("HeimDefender", "BoltType", 0, new ConfigDescription(
            "Ammunition for the next automatic reload: 0 Wooden, 1 Black Metal, 2 Flametal, 3 Bloodgold.",
            new AcceptableValueRange<int>(0, BoltLabels.Length - 1)));
        BoltsPerReload = file.Bind("HeimDefender", "BoltsPerReload", 40,
            new ConfigDescription("Target bolt count after a free refill (1 to 40), capped by magazine capacity.", new AcceptableValueRange<int>(1, 40)));
        DebugMode = file.Bind("HeimDefender", "DebugMode", false, "Record targeting, ownership and ammunition diagnostics.");
        Debug = DebugLoggingResolver.Resolve(file, "HeimDefender", DiagnosticsSettings.Bind(file), log, DebugMode);
        Directory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file.ConfigFilePath)!, "HeimDefender");
    }
}
