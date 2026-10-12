using BepInEx.Configuration;

namespace HeimKit.Configuration;

public sealed class DebugLoggingPolicy
{
    private readonly ConfigEntry<bool> _global;
    private readonly ConfigEntry<bool>? _feature;
    public bool Suppressed { get; }
    public bool ConfiguredEnabled => (_global.Value || _feature?.Value == true) && !Suppressed;
    public bool Enabled => ConfiguredEnabled && HeimKit.Shared.DiagnosticLog.ServerActivity.Allowed;
    public DebugLoggingPolicy(ConfigEntry<bool> global, bool suppressed, ConfigEntry<bool>? feature = null)
    { _global = global; _feature = feature; Suppressed = suppressed; }
}
