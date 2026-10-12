using HeimKit.Shared.Players;
namespace HeimKit.Features.AFKRaidWatch.Models;
public sealed class RaidWatchStatus
{
    public int? Connected;
    public long Candidates, Suppressed;
    public ObservationCounters? Hooks;
    public RaidStartObservation? Outcome;
    public RaidScheduleObservation? Schedule;
}
