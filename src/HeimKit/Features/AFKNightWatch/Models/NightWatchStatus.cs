using HeimKit.Shared.Players;
namespace HeimKit.Features.AFKNightWatch.Models;
public sealed class NightWatchStatus
{
    public SleepDecision? Vote;
    public float? VoteAge;
    public long Votes, Outcomes;
    public int? Players;
    public bool MajorityEnabled, ExclusionEnabled;
    public ObservationCounters? Hooks;
    public NativeSleepObservation? Sleep, SleepOutcome;
}
