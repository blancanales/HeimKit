using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Shared;

namespace HeimKit.Configuration;

public sealed class DebugLoggingPolicy
{
    private readonly ConfigEntry<bool> _global;
    private readonly ConfigEntry<bool>? _feature;
    public bool Suppressed { get; }
    public bool ConfiguredEnabled => (_global.Value || _feature?.Value == true) && !Suppressed;
    public bool Enabled => ConfiguredEnabled && HeimKit.Shared.DiagnosticLog.ServerActivity.Allowed;
    // One configuration declaration is deliberately visible at Debug even when suppression
    // disables this feature's recurring diagnostics. Loader severity filters still apply.
    public void ReportStartup(ManualLogSource logger, string name, bool featureEnabled)
    {
        if (!_global.Value && _feature?.Value != true && !Suppressed) return;
        if (ConfiguredEnabled) DebugLoggingDelivery.Report(logger, name);
        string source = Suppressed ? "hidden_override" : _global.Value && _feature?.Value == true
            ? "global_and_feature" : _feature?.Value == true ? "feature" : "global";
        DiagnosticLog.Write(logger, name, DiagnosticEvent.Lifecycle,
            Suppressed ? "Debug logging suppressed by DisableDebugLogging."
                : source == "feature" ? "Debug logging enabled by feature DebugMode."
                : source == "global" ? "Debug logging enabled by global DebugMode."
                : "Debug logging enabled by global and feature DebugMode.",
            new (string, object?)[] {
                ("source", source), ("global_debug_mode", _global.Value), ("feature_debug_mode", _feature?.Value == true),
                ("disable_debug_logging", Suppressed), ("configured_debug_enabled", ConfiguredEnabled),
                ("feature_enabled", featureEnabled), ("config_section", _feature?.Definition.Section)
            }, LogLevel.Debug);
    }
    public DebugLoggingPolicy(ConfigEntry<bool> global, bool suppressed, ConfigEntry<bool>? feature = null)
    { _global = global; _feature = feature; Suppressed = suppressed; }
}
