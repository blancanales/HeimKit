using HeimKit.Shared.Players;
using HeimKit.Shared.Players.Models;
using HeimKit.Features.AFKNightWatch.Models;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
namespace HeimKit.Features.AFKNightWatch;
public sealed class AFKNightWatchSettings
{
    public bool RequiresActivity => Enabled.Value && ExcludeAFKFromSleep.Value;
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<bool> MajoritySleepEnabled { get; }
    public ConfigEntry<bool> ExcludeAFKFromSleep { get; }
    public ConfigEntry<int> RequiredSleepingPercent { get; }
    public ConfigEntry<bool> RequireMoreThanPercent { get; }
    public ConfigEntry<int> SleepDelaySeconds { get; }
    public ConfigEntry<bool> DebugMode { get; }
    public ConfigEntry<bool> GlobalDebugMode { get; }
    public DebugLoggingPolicy DebugLogging { get; }
    public AFKNightWatchSettings(ConfigFile config, ManualLogSource logger)
    {
        Enabled = config.Bind("AFKNightWatch", "Enabled", true, "Enable majority sleep and AFK sleep exclusion. Restart after edits.");
        MajoritySleepEnabled = config.Bind("AFKNightWatch", "MajoritySleepEnabled", true, "Use the configured sleep quorum; otherwise require all non-excluded players. Restart after edits.");
        ExcludeAFKFromSleep = config.Bind("AFKNightWatch", "ExcludeAFKFromSleep", true, "Exclude inferred-idle players from the sleep vote. Restart after edits.");
        RequiredSleepingPercent = config.Bind("AFKNightWatch", "RequiredSleepingPercent", 50, new ConfigDescription("Sleeping threshold percentage; 100 requires all participants. Restart after edits.", new AcceptableValueRange<int>(1, 100)));
        RequireMoreThanPercent = config.Bind("AFKNightWatch", "RequireMoreThanPercent", true, "Strictly exceed the threshold below 100 percent. Restart after edits.");
        SleepDelaySeconds = config.Bind("AFKNightWatch", "SleepDelaySeconds", 30, new ConfigDescription("Wait this many real seconds after the sleep threshold is met while some players are not in bed. Start immediately when everyone is in bed; reset the wait if the threshold is lost. Restart after edits.", new AcceptableValueRange<int>(0, 300)));
        GlobalDebugMode = DiagnosticsSettings.Bind(config);
        DebugMode = config.Bind("AFKNightWatch", "DebugMode", false, "Enable diagnostics for this feature even when global DebugMode is off. The hidden suppression override still takes precedence. Restart after edits.");
        DebugLogging = DebugLoggingResolver.Resolve(config, "AFKNightWatch", GlobalDebugMode, logger, DebugMode);
    }
}
