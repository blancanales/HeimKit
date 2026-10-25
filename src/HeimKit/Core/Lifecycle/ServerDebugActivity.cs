using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;

namespace HeimKit.Core.Lifecycle;

/// <summary>Shared automatic-debug grace period, independent of feature enablement.</summary>
public sealed class ServerDebugActivity
{
    public const double EmptyGraceSeconds = 300;
    private double? _emptySince = 0;
    private double _lastObserved;
    public bool Allowed { get; private set; } = true;

    public void Observe(double now, bool? hasConnectedPlayers)
    {
        if (double.IsNaN(now) || double.IsInfinity(now)) return;
        if (now < _lastObserved) Reset(now);
        _lastObserved = now;
        if (hasConnectedPlayers == true)
        {
            _emptySince = null;
            Allowed = true;
            return;
        }
        if (hasConnectedPlayers == false && !_emptySince.HasValue) _emptySince = now;
        // Unknown population does not start a new empty interval. The initial
        // startup grace still expires if server context has not become available.
        if (_emptySince.HasValue) Allowed = now - _emptySince.Value < EmptyGraceSeconds;
    }

    public void Reset(double now = 0)
    {
        _emptySince = now;
        _lastObserved = now;
        Allowed = true;
    }
}
