using BepInEx.Configuration;

namespace HeimKit.Configuration;

public sealed class DebugLoggingPolicy
{
    private readonly ConfigEntry<bool> _global;
    public bool Suppressed { get; }
    public bool Enabled => _global.Value && !Suppressed;
    public DebugLoggingPolicy(ConfigEntry<bool> global, bool suppressed)
    { _global = global; Suppressed = suppressed; }
}
