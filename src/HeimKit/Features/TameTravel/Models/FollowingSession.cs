using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Features.TameTravel.Models;

internal sealed class FollowingSession
{
    public AnimalTransfer Lease = null!;
    public bool Acquired;
    public bool Started;
    public bool Stopped;
    public float Requested;
    // When a stay or wait arrived while the server was simulating; -1 when none is pending.
    public float StopRequested = -1;
    public float ContextAt = float.NegativeInfinity;
    public float NextContext;
    public float UnreadySince = -1;
    public float StopDistance;
    public float ResumeDistance;
    public UnityEngine.Vector3 ContextPosition;
}
