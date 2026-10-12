using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
namespace HeimKit.Features.AFKRaidWatch;
public sealed class AFKRaidWatchSettings
{
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<bool> DebugMode { get; }
    public DebugLoggingPolicy DebugLogging { get; }
    public AFKRaidWatchSettings(ConfigFile config, ManualLogSource logger)
    {
        Enabled = config.Bind("AFKRaidWatch", "Enabled", true, "Protect inferred-idle-only locations from new normal automatic random raids. Restart after edits.");
        DebugMode = config.Bind("AFKRaidWatch", "DebugMode", false, "Enable raid diagnostics; hidden suppression takes precedence. Restart after edits.");
        DebugLogging = DebugLoggingResolver.Resolve(config, "AFKRaidWatch", DiagnosticsSettings.Bind(config), logger, DebugMode);
    }
}
