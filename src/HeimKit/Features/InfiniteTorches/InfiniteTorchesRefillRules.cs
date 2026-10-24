using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;

namespace HeimKit.Features.InfiniteTorches;

internal static class InfiniteTorchesRefillRules
{
    internal static bool IsFireplace(string name) => name == "fire_pit" || name == "hearth" || name == "bonfire";

    internal static string? RejectionReason(string name, bool canRefill, bool infiniteFuel,
        bool canTurnOff, bool hasWetFlames, float maximumFuel, bool includeFireplaces = false)
    {
        bool brazier = name == "piece_brazierceiling01" || name == "piece_brazierfloor01" ||
            name == "piece_brazierfloor02";
        bool fireplace = IsFireplace(name);
        return fireplace && !includeFireplaces ? "fire type disabled by fireplace settings"
            : !canRefill ? "refilling not supported"
            : infiniteFuel ? "already has infinite fuel"
            : canTurnOff ? "toggle-off behavior is outside the supported light-source scope"
            : hasWetFlames && !brazier && !fireplace ? "wet-flame behavior is outside the supported light-source scope"
            : maximumFuel <= 0f || float.IsNaN(maximumFuel) || float.IsInfinity(maximumFuel)
                ? "invalid maximum fuel" : null;
    }
}
