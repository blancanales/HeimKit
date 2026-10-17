using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;

namespace HeimKit.Features.SharedMap;

public sealed class SharedMapSettings
{
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<bool> DebugMode { get; }
    public ConfigEntry<bool> GlobalDebugMode { get; }
    public DebugLoggingPolicy DebugLogging { get; }

    public SharedMapSettings(ConfigFile config, ManualLogSource? logger = null)
    {
        Enabled = config.Bind("SharedMap", "Enabled", false,
            "Enable the SharedMap module lifecycle. Mapping behavior is not implemented. Restart after edits.");
        GlobalDebugMode = DiagnosticsSettings.Bind(config);
        DebugMode = config.Bind("SharedMap", "DebugMode", false,
            "Enable this module's diagnostics even when global DebugMode is off. The hidden suppression override still applies. Restart after edits.");
        DebugLogging = DebugLoggingResolver.Resolve(config, "SharedMap", GlobalDebugMode, logger, DebugMode);
    }
}
