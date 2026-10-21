using System;
using HeimKit.Features.ForestRenewal.Models;

namespace HeimKit.Features.ForestRenewal;

internal static class ForestRenewalCommitRules
{
    public static bool Ready(RenewalPlacement placement, long durableRevision)
        => placement.RequiredRevision > 0 && durableRevision >= placement.RequiredRevision &&
            placement.Decision.State == 2 && !placement.MutationStarted;

    public static void RestorePending(RenewalPlacement placement)
    {
        if (placement.MutationStarted || placement.Decision.State != 2 || placement.PendingState != 0 && placement.PendingState != 3)
            throw new InvalidOperationException("An attempted or invalid renewal cannot return to pending.");
        placement.Decision.State = placement.PendingState;
        placement.Decision.DecisionTicks = placement.PreviousDecisionTicks;
    }
}
