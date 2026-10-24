using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Players;
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
