using System;

namespace HeimKit.Features.TameTravel;

internal static class TameTravelFollowRules
{
    public static (float Stop, float Resume) Distances(float stop, float resume, float animalExtent, float playerExtent)
    {
        foreach (float value in new[] { stop, resume, animalExtent, playerExtent })
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(stop));
        float effective = Math.Max(stop, animalExtent + playerExtent + 1);
        return (effective, Math.Max(resume, effective + 1));
    }
    public static bool Stopped(bool stopped, float distanceSquared, float stop, float resume)
        => stopped ? distanceSquared <= resume * resume : distanceSquared <= stop * stop;
}
