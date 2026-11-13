using System;
using System.Globalization;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

// Observed reserve only: never drain, regenerate, load or take ownership of a root.
internal static class SharedMapAncientRoot
{
    internal static int? Percentage(float level, float maximum)
    {
        if (!SharedMapExploration.Finite(level) || !SharedMapExploration.Finite(maximum) ||
            maximum <= 0 || level < 0 || level > maximum) return null;
        return (int)Math.Floor((double)level * 100 / maximum);
    }
    internal static string Label(ResourceDeposit root) => root.Depleted ? "Ancient Root (removed)" :
        "Ancient Root " + (root.SapPercent.HasValue ? root.SapPercent.Value.ToString(CultureInfo.InvariantCulture) : "?") + "%";
}
