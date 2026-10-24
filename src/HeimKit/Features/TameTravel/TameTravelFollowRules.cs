using System;

namespace HeimKit.Features.TameTravel;

internal static class TameTravelFollowRules
{
    public static (float Stop, float Resume) Distances(float stop, float resume, float animalExtent, float playerExtent, bool lox = false)
    {
        foreach (float value in new[] { stop, resume, animalExtent, playerExtent })
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(stop));
        // A lox's collision capsule is much smaller than its visible body.
        // Keep comfortable spacing as well as the scaled physical clearance.
        float effective = Math.Max(Math.Max(stop, lox ? 6 : 0), animalExtent + playerExtent + 1);
        return (effective, Math.Max(Math.Max(resume, lox ? 8 : 0), effective + (lox ? 2 : 1)));
    }
    public static bool Stopped(bool stopped, float distanceSquared, float stop, float resume)
        => stopped ? distanceSquared <= resume * resume : distanceSquared <= stop * stop;
}
