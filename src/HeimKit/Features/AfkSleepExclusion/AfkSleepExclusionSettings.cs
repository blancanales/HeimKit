using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
namespace HeimKit.Features.AfkSleepExclusion;
public sealed class AfkSleepExclusionSettings
{
    public ConfigEntry<bool> Enabled { get; }
    public DebugLoggingPolicy DebugLogging { get; }
    public PlayerActivitySettings Activity { get; }
    public AfkSleepExclusionSettings(ConfigFile config, ManualLogSource logger)
    {
        Enabled = config.Bind("AfkSleepExclusion", "Enabled", true, "Enable AfkSleepExclusion independently. Restart after edits.");
        DebugLogging = DebugLoggingResolver.Resolve(config, "AfkSleepExclusion", DiagnosticsSettings.Bind(config), logger);
        Activity = new PlayerActivitySettings(config);
    }
}
