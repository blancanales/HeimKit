using BepInEx.Configuration;
using HeimKit.Shared;

namespace HeimKit.Configuration;

public sealed class PlayerActivitySettings
{
    public ConfigEntry<float> IdleTimeoutSeconds { get; }
    public ConfigEntry<float> JoinGraceSeconds { get; }
    public ConfigEntry<float> MovementThresholdMeters { get; }
    public ConfigEntry<float> LookThresholdDegrees { get; }
    public ConfigEntry<float> SampleIntervalSeconds { get; }
    public PlayerActivitySettings(ConfigFile config)
    {
        IdleTimeoutSeconds = config.Bind("PlayerActivity", "IdleTimeoutSeconds", 300f, new ConfigDescription("Seconds without observed activity before inferred idle. Restart after edits.", new AcceptableValueRange<float>(30f, 3600f)));
        JoinGraceSeconds = config.Bind("PlayerActivity", "JoinGraceSeconds", 60f, new ConfigDescription("Minimum active grace after join/loading/death recovery. Restart after edits.", new AcceptableValueRange<float>(0f, 600f)));
        MovementThresholdMeters = config.Bind("PlayerActivity", "MovementThresholdMeters", 0.5f, new ConfigDescription("Cumulative world-position movement counted as activity.", new AcceptableValueRange<float>(0.1f, 5f)));
        LookThresholdDegrees = config.Bind("PlayerActivity", "LookThresholdDegrees", 10f, new ConfigDescription("Replicated body rotation change counted as activity; not all camera input is visible.", new AcceptableValueRange<float>(1f, 90f)));
        SampleIntervalSeconds = config.Bind("PlayerActivity", "SampleIntervalSeconds", 1f, new ConfigDescription("Shared activity sampling interval; restart after edits.", new AcceptableValueRange<float>(0.2f, 5f)));
    }
    public PlayerActivityOptions Snapshot() => new(IdleTimeoutSeconds.Value, JoinGraceSeconds.Value, MovementThresholdMeters.Value, LookThresholdDegrees.Value, SampleIntervalSeconds.Value);
}
