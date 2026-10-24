using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;

namespace HeimKit.Features.TameTravel;

public sealed class TameTravelSettings
{
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<bool> DebugMode { get; }
    public DebugLoggingPolicy DebugLogging { get; }
    public ConfigEntry<float> CommandRangeMeters { get; }
    public ConfigEntry<bool> FollowSpacingEnabled { get; }
    public ConfigEntry<float> FollowStopDistanceMeters { get; }
    public ConfigEntry<float> FollowResumeDistanceMeters { get; }
    public ConfigEntry<int> MaxControlledFollowers { get; }
    public ConfigEntry<float> FollowerRangeMeters { get; }
    public ConfigEntry<float> FollowConsentSeconds { get; }
    public ConfigEntry<float> TravelWindowSeconds { get; }
    public ConfigEntry<int> MaxFollowersPerTraversal { get; }
    public TameTravelSettings(ConfigFile config, ManualLogSource logger)
    {
        const string section = "TameTravel";
        Enabled = config.Bind(section, "Enabled", true, "Enable validated portal travel for explicitly following tames. Restart after edits.");
        DebugMode = config.Bind(section, "DebugMode", false, "Enable feature diagnostics through the shared debug policy.");
        CommandRangeMeters = config.Bind(section, "CommandRangeMeters", 8f, new ConfigDescription("Maximum follow/wait selection distance. Stay addresses your associated animals within 32 metres.", new AcceptableValueRange<float>(2, 12)));
        FollowSpacingEnabled = config.Bind(section, "FollowSpacingEnabled", true, "Use bounded server ownership to apply follow spacing after an explicit follow command. Restart after edits.");
        FollowStopDistanceMeters = config.Bind(section, "FollowStopDistanceMeters", 4f, new ConfigDescription("Horizontal follow stopping distance; enlarged when needed for animal and player clearance.", new AcceptableValueRange<float>(3, 12)));
        FollowResumeDistanceMeters = config.Bind(section, "FollowResumeDistanceMeters", 5f, new ConfigDescription("Horizontal distance to resume following; effective value is at least the stop distance plus one metre.", new AcceptableValueRange<float>(4, 16)));
        MaxControlledFollowers = config.Bind(section, "MaxControlledFollowers", 8, new ConfigDescription("Global cap on server-controlled followers; native following remains when capacity is unavailable.", new AcceptableValueRange<int>(1, 16)));
        FollowerRangeMeters = config.Bind(section, "FollowerRangeMeters", 20f, new ConfigDescription("Maximum follower distance from player and source portal at entry.", new AcceptableValueRange<float>(5, 32)));
        FollowConsentSeconds = config.Bind(section, "FollowConsentSeconds", 300f, new ConfigDescription("Optional portal-wait consent lifetime; one traversal consumes it. Active follow permission lasts for the current player session until stopped or invalidated.", new AcceptableValueRange<float>(30, 1800)));
        TravelWindowSeconds = config.Bind(section, "TravelWindowSeconds", 30f, new ConfigDescription("Maximum time to observe corresponding portal arrival.", new AcceptableValueRange<float>(15, 60)));
        MaxFollowersPerTraversal = config.Bind(section, "MaxFollowersPerTraversal", 8, new ConfigDescription("Maximum tames reserved for one traversal.", new AcceptableValueRange<int>(1, 16)));
        foreach (float n in new[] { CommandRangeMeters.Value, FollowStopDistanceMeters.Value, FollowResumeDistanceMeters.Value, FollowerRangeMeters.Value, FollowConsentSeconds.Value, TravelWindowSeconds.Value })
            if (float.IsNaN(n) || float.IsInfinity(n)) throw new ArgumentException("TameTravel settings must be finite.");
        DebugLogging = DebugLoggingResolver.Resolve(config, section, DiagnosticsSettings.Bind(config), logger, DebugMode);
    }
}
