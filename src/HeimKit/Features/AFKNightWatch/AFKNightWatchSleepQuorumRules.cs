using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Players;
using HeimKit.Core.Players.Models;
using HeimKit.Features.AFKNightWatch.Models;
using System;
using System.Collections.Generic;

namespace HeimKit.Features.AFKNightWatch;

public static class AFKNightWatchSleepQuorumRules
{
    public static int Required(int participants, int percentage, bool strict)
    {
        if (participants <= 0) return 0;
        percentage = Math.Max(1, Math.Min(100, percentage));
        if (percentage == 100) return participants;
        double share = (double)participants * percentage / 100;
        return Math.Min(participants, Math.Max(1, strict ? (int)Math.Floor(share) + 1 : (int)Math.Ceiling(share)));
    }
    public static SleepDecision Evaluate(IEnumerable<PlayerActivityView> players, int percentage, bool strict, bool excludeIdle)
    {
        var result = new SleepDecision();
        foreach (var player in players)
        {
            if (!player.Known) result.Unknown = true;
            if (excludeIdle && player.Idle && !player.InBed) { result.ExcludedIdle++; continue; }
            result.Participants++;
            if (player.Known && player.Alive && player.InBed) result.Sleepers++;
        }
        result.Required = Required(result.Participants, percentage, strict);
        result.Met = !result.Unknown && result.Participants > 0 && result.Sleepers > 0 && result.Sleepers >= result.Required;
        return result;
    }
}
