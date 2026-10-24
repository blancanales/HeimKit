using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Players;
namespace HeimKit.Features.AFKRaidWatch.Models;
public sealed class RaidWatchStatus
{
    public int? Connected;
    public long Candidates, Suppressed;
    public ObservationCounters? Hooks;
    public RaidStartObservation? Outcome;
    public RaidScheduleObservation? Schedule;
}
