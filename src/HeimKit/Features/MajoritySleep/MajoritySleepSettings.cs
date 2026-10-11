using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
namespace HeimKit.Features.MajoritySleep;
public sealed class MajoritySleepSettings
{
    public ConfigEntry<bool> Enabled { get; }
    public DebugLoggingPolicy DebugLogging { get; }
    public PlayerActivitySettings Activity { get; }
    public ConfigEntry<int> RequiredSleepingPercent { get; }
    public ConfigEntry<bool> RequireMoreThanPercent { get; }
    public MajoritySleepSettings(ConfigFile config, ManualLogSource logger)
    {
        Enabled = config.Bind("MajoritySleep", "Enabled", true, "Enable MajoritySleep independently. Restart after edits.");
        DebugLogging = DebugLoggingResolver.Resolve(config, "MajoritySleep", DiagnosticsSettings.Bind(config), logger);
        Activity = new PlayerActivitySettings(config);
        RequiredSleepingPercent = config.Bind("MajoritySleep", "RequiredSleepingPercent", 50, new ConfigDescription("Sleeping threshold percentage; 100 requires all participants. Restart after edits.", new AcceptableValueRange<int>(1, 100)));
        RequireMoreThanPercent = config.Bind("MajoritySleep", "RequireMoreThanPercent", true, "Strictly exceed the threshold below 100 percent. Restart after edits.");
    }
}
