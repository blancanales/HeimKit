using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Players;
namespace HeimKit.Core.Players.Models;

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
