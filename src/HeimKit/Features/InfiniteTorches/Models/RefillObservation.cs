using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Features.InfiniteTorches.Models;

public readonly struct RefillObservation
{
    public readonly long Owner;
    public readonly float Fuel;
    public RefillObservation(long owner, float fuel) { Owner = owner; Fuel = fuel; }
}
