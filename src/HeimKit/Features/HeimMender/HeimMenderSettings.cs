using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;

namespace HeimKit.Features.HeimMender;

public sealed class HeimMenderSettings
{
    public readonly List<ConfigEntryBase> Entries = new();
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<bool> DebugMode { get; }
    public DebugLoggingPolicy DebugLogging { get; }
    public ConfigEntry<bool> AutomaticEnabled { get; }
    public ConfigEntry<float> IntervalSeconds { get; }
    public ConfigEntry<float> RepairChancePercent { get; }
    public ConfigEntry<int> MaxRepairsPerCycle { get; }
    public ConfigEntry<float> ScanRangeMeters { get; }
    public ConfigEntry<float> DamageQuietSeconds { get; }
    public ConfigEntry<float> HostileClearanceMeters { get; }
    public ConfigEntry<float> AdminRadiusMeters { get; }
    public ConfigEntry<int> AdminMaxPieces { get; }
    public ConfigEntry<int> RepairRequestId { get; }
    public ConfigEntry<string> RepairTarget { get; }
    public HeimMenderSettings(ConfigFile config, ManualLogSource logger)
    {
        ConfigEntry<T> Bind<T>(string key, T value, string description, AcceptableValueBase? range = null)
        { var entry = config.Bind("HeimMender", key, value, new ConfigDescription(description + " Reloads during play.", range)); Entries.Add(entry); return entry; }
        Enabled = Bind("Enabled", true, "Enable HeimMender.");
        DebugMode = Bind("DebugMode", false, "Enable shared-policy feature diagnostics.");
        AutomaticEnabled = Bind("AutomaticEnabled", true, "Enable gradual automatic repairs.");
        IntervalSeconds = Bind("IntervalSeconds", 300f, "Seconds between automatic repair cycles.", new AcceptableValueRange<float>(10, 86400));
        RepairChancePercent = Bind("RepairChancePercent", 50f, "Independent chance per damaged candidate per cycle.", new AcceptableValueRange<float>(0, 100));
        MaxRepairsPerCycle = Bind("MaxRepairsPerCycle", 10, "Maximum automatic requests per cycle.", new AcceptableValueRange<int>(1, 200));
        ScanRangeMeters = Bind("ScanRangeMeters", 64f, "Nearby rotating-player scan radius.", new AcceptableValueRange<float>(16, 128));
        DamageQuietSeconds = Bind("DamageQuietSeconds", 60f, "Automatic deferral after observed health loss.", new AcceptableValueRange<float>(0, 3600));
        HostileClearanceMeters = Bind("HostileClearanceMeters", 30f, "Automatic deferral around untamed MonsterAI; zero disables.", new AcceptableValueRange<float>(0, 64));
        AdminRadiusMeters = Bind("AdminRadiusMeters", 64f, "Default administrative repair radius.", new AcceptableValueRange<float>(1, 128));
        AdminMaxPieces = Bind("AdminMaxPieces", 200, "Maximum native requests per administrative batch.", new AcceptableValueRange<int>(1, 2000));
        RepairRequestId = Bind("RepairRequestId", 0, "Increase while running to request a batch. Startup never replays it.", new AcceptableValueRange<int>(0, int.MaxValue));
        RepairTarget = Bind("RepairTarget", "", "Exact ready player name or peer UID; empty requires one ready player.");
        foreach (var entry in Entries)
            if (entry.BoxedValue is float value && (float.IsNaN(value) || float.IsInfinity(value))) throw new System.ArgumentException("HeimMender settings must be finite.");
        DebugLogging = DebugLoggingResolver.Resolve(config, "HeimMender", DiagnosticsSettings.Bind(config), logger, DebugMode);
    }
}
