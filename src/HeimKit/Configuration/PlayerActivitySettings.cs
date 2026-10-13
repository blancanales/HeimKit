using BepInEx.Configuration;
using HeimKit.Shared.Players.Models;
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
        IdleTimeoutSeconds = config.Bind("PlayerActivity", "IdleTimeoutSeconds", 300f, new ConfigDescription("How long a player must go without activity the server can detect before HeimKit considers them idle. Default: 300 seconds (5 minutes). Lower values mark players idle sooner; higher values wait longer. The join/recovery grace must also have ended. Restart after edits.", new AcceptableValueRange<float>(30f, 3600f)));
        JoinGraceSeconds = config.Bind("PlayerActivity", "JoinGraceSeconds", 60f, new ConfigDescription("Minimum time a player is kept active after joining or recovering from loading, death or unreliable player information. Default: 60 seconds (1 minute). Both this grace period and the idle timeout must have passed before the player can be considered idle; the two times are not added together. Restart after edits.", new AcceptableValueRange<float>(0f, 600f)));
        MovementThresholdMeters = config.Bind("PlayerActivity", "MovementThresholdMeters", 0.5f, new ConfigDescription("How far a player position must change from the last activity position to count as movement. Default: 0.5 meters. Small position changes can build up to this threshold. Lower values detect smaller movements; higher values ignore more small movements. Being carried or moved by the world also counts. Restart after edits.", new AcceptableValueRange<float>(0.1f, 5f)));
        LookThresholdDegrees = config.Bind("PlayerActivity", "LookThresholdDegrees", 10f, new ConfigDescription("How much a player character must turn for the server to count it as activity. Default: 10 degrees. Lower values detect smaller turns; higher values require larger turns. This checks character direction reported to the server, so moving only the camera may not count. Restart after edits.", new AcceptableValueRange<float>(1f, 90f)));
        SampleIntervalSeconds = config.Bind("PlayerActivity", "SampleIntervalSeconds", 1f, new ConfigDescription("How often HeimKit checks player state for activity, in seconds. Default: 1 second. Lower values check more often and use more server processing; higher values check less often and may delay detecting changes. Checks run whenever an enabled feature needs idle detection, including during the day. Restart after edits.", new AcceptableValueRange<float>(0.2f, 5f)));
    }
    public PlayerActivityOptions Options() => new(IdleTimeoutSeconds.Value, JoinGraceSeconds.Value, MovementThresholdMeters.Value, LookThresholdDegrees.Value, SampleIntervalSeconds.Value);
}
