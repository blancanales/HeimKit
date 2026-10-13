using System;

namespace HeimKit.Features.InfiniteTorches;

internal static class InfiniteTorchesRefillRules
{
    internal static string? RejectionReason(string name, bool canRefill, bool infiniteFuel,
        bool canTurnOff, bool hasWetFlames, float maximumFuel)
    {
        bool brazier = name == "piece_brazierceiling01" || name == "piece_brazierfloor01" ||
            name == "piece_brazierfloor02";
        return !canRefill ? "refilling not supported"
            : infiniteFuel ? "already has infinite fuel"
            : canTurnOff ? "toggle-off behavior is outside the supported light-source scope"
            : hasWetFlames && !brazier ? "wet-flame behavior is outside the supported light-source scope"
            : maximumFuel <= 0f || float.IsNaN(maximumFuel) || float.IsInfinity(maximumFuel)
                ? "invalid maximum fuel" : null;
    }
}
