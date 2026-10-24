using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Features.AFKRaidWatch.Models;
public sealed class RaidScheduleObservation
{
    public float ObservedAt, Timer, IntervalMinutes, EventRate, Chance;
    public string Gate => EventRate <= 0 ? "native_automatic_raids_disabled" : Timer <= IntervalMinutes * 60 * EventRate ? "awaiting_native_interval" : "native_interval_threshold_reached";
}
