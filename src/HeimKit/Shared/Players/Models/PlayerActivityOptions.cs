using HeimKit.Shared.Players;
namespace HeimKit.Shared.Players.Models;

public sealed class PlayerActivityOptions
{
    public float Timeout { get; }
    public float Grace { get; }
    public float Movement { get; }
    public float Look { get; }
    public float Interval { get; }
    public PlayerActivityOptions(float timeout, float grace, float movement, float look, float interval)
    {
        if (!PlayerActivityTracker.Finite(timeout) || !PlayerActivityTracker.Finite(grace) || !PlayerActivityTracker.Finite(movement) ||
            !PlayerActivityTracker.Finite(look) || !PlayerActivityTracker.Finite(interval) || timeout <= 0 || grace < 0 || movement <= 0 || look <= 0 || interval <= 0)
            throw new System.ArgumentOutOfRangeException(nameof(timeout), "Activity settings must be finite with positive thresholds and a nonnegative grace.");
        Timeout = timeout; Grace = grace; Movement = movement; Look = look; Interval = interval;
    }
}
