using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
namespace HeimKit.Features.AfkRaidProtection;
public sealed class AfkRaidProtectionSettings
{
    public ConfigEntry<bool> Enabled { get; }
    public DebugLoggingPolicy DebugLogging { get; }
    public PlayerActivitySettings Activity { get; }
    public AfkRaidProtectionSettings(ConfigFile config, ManualLogSource logger)
    {
        Enabled = config.Bind("AfkRaidProtection", "Enabled", true, "Enable AfkRaidProtection independently. Restart after edits.");
        DebugLogging = DebugLoggingResolver.Resolve(config, "AfkRaidProtection", DiagnosticsSettings.Bind(config), logger);
        Activity = new PlayerActivitySettings(config);
    }
}
