using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Features.AFKNightWatch;
namespace HeimKit.Features.AFKNightWatch.Models;

public sealed class SleepDecision
{
    public int Participants { get; set; }
    public int Sleepers { get; set; }
    public int Required { get; set; }
    public int ExcludedIdle { get; set; }
    public bool Unknown { get; set; }
    public bool Met { get; set; }
    public bool AllowedAfterDelay { get; set; }
    public float DelayRemaining { get; set; }
}
