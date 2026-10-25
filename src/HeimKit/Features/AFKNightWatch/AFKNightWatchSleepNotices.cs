using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using BepInEx.Logging;
using HeimKit.Features.AFKNightWatch.Models;
namespace HeimKit.Features.AFKNightWatch;

public sealed class AFKNightWatchSleepNotices
{
    private readonly ManualLogSource _logger;
    private readonly Action<SleepDecision>? _evidence;
    private bool _known, _skipping;
    public AFKNightWatchSleepNotices(ManualLogSource logger, Action<SleepDecision>? evidence = null)
    { _logger = logger; _evidence = evidence; }
    public void Observe(bool sleeping, bool? skipping, SleepDecision? vote = null)
    {
        if (!skipping.HasValue) { Reset(); return; }
        bool active = sleeping && skipping.Value;
        if (!_known) { _known = true; _skipping = active; return; }
        if (active == _skipping) return;
        _skipping = active;
        if (!active || vote == null || !vote.AllowedAfterDelay || vote.Unknown ||
            vote.Sleepers >= vote.Participants + vote.ExcludedIdle) return;
        DiagnosticLog.Write(_logger, "AFKNightWatch", "Sleep started under the configured rules while some players were not in bed.");
        _evidence?.Invoke(vote);
    }
    public void Reset() { _known = false; _skipping = false; }
}
