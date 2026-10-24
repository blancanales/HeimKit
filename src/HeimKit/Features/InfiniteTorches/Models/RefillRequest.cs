using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Features.InfiniteTorches.Models;

internal readonly struct RefillRequest
{
    public readonly long Owner;
    public readonly float Before, Target, SentAt;
    public RefillRequest(long owner, float before, float target, float now)
    { Owner = owner; Before = before; Target = target; SentAt = now; }
}
