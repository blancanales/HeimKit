using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

internal static class TameTravelPlacementRules
{
    internal const int MaximumEscapeProbes = 64;
    internal const int MaximumCandidatesPerPass = 64;
    internal const int PreferredCandidates = 392;
    internal const float LandingRadius = 48, ContextRadius = 56;
    internal static float GridSpacing(float extent) => Math.Max(2, Math.Min(3, extent));
    internal static int CandidateCount(float extent)
    {
        int width = 2 * (int)Math.Ceiling(LandingRadius / GridSpacing(extent)) + 1;
        return PreferredCandidates + width * width * 2;
    }
    // Prefer forward/left/right ground in the portal's own frame. The trip seed
    // varies side order, angle and distance without mutating Unity's random state.
    internal static Vector3 Candidate(int index, float extent, Vector3 exit, Vector3 traveller, Vector3 forward, int variation)
    {
        var right = new Vector3(forward.z, 0, -forward.x);
        if (index < PreferredCandidates)
        {
            uint seed = unchecked((uint)variation * 747796405u + 2891336453u);
            float preferredSide = (seed & 1) == 0 ? -1 : 1;
            int preferredRing = index / 28, slot = index % 28;
            // Finish each densely sampled nearby arc before trying a more distant ring.
            float angle = slot == 2 ? 0 : slot < 2 ? .15f * preferredSide * (slot == 0 ? 1 : -1) :
                ((slot - 1) / 2) * .075f * preferredSide * (slot % 2 == 1 ? 1 : -1);
            angle += ((seed >> 8 & 255) / 255f - .5f) * .16f;
            float distance = 4 + extent + preferredRing * .5f + (seed >> 16 & 255) / 255f * .5f;
            return exit + (forward * (float)Math.Cos(angle) + right * (float)Math.Sin(angle)) * distance;
        }
        // Retain broad building/traveller fallbacks, subject to the same strict
        // front clearance. Searching never authorizes a rear-side landing.
        index -= PreferredCandidates;
        var anchor = index % 2 == 0 ? exit : traveller;
        int cell = index / 2;
        if (cell == 0) return anchor;
        int ring = (int)Math.Ceiling((Math.Sqrt(cell + 1) - 1) / 2);
        int edge = 2 * ring, offset = cell - (2 * ring - 1) * (2 * ring - 1);
        int side = offset / edge, step = offset % edge;
        int x = side == 0 ? -ring + step : side == 1 ? ring : side == 2 ? ring - step : -ring;
        int z = side == 0 ? -ring : side == 1 ? -ring + step : side == 2 ? ring : ring - step;
        return anchor + (right * x + forward * z) * GridSpacing(extent);
    }
    internal static bool InFront(Vector3 point, Vector3 portal, Vector3 forward, float clearance)
        => PlayerWorldScope.Finite(point) && PlayerWorldScope.Finite(portal) && PlayerWorldScope.Finite(forward) &&
            PlayerWorldScope.Finite(clearance) && clearance >= 0 && forward.sqrMagnitude > .001f &&
            Vector3.Dot(point - portal, forward) >= clearance;
    internal static bool SupportedFootprint(float centerHeight, float sampleHeight, Vector3 offset, Vector3 normal)
    {
        if (normal.y < .8f || !PlayerWorldScope.Finite(sampleHeight)) return false;
        float expected = centerHeight - (normal.x * offset.x + normal.z * offset.z) / normal.y;
        return Math.Abs(sampleHeight - expected) <= .5f;
    }
    internal static bool HasEscape(Vector3 start, Vector3 outward, float distance,
        Func<Vector3, Vector3?> ground, Func<Vector3, Vector3, bool> clear, ref int probes)
    {
        if (!PlayerWorldScope.Finite(start) || !PlayerWorldScope.Finite(outward) || !PlayerWorldScope.Finite(distance) ||
            distance < 3 || distance > 32) return false;
        outward.y = 0;
        if (outward.sqrMagnitude < .001f) return false;
        outward = outward.normalized;
        var right = new Vector3(outward.z, 0, -outward.x);
        int steps = (int)Math.Ceiling(distance);
        for (int direction = 0; direction < 8; direction++)
        {
            double angle = direction * Math.PI / 4;
            var heading = outward * (float)Math.Cos(angle) + right * (float)Math.Sin(angle);
            var previous = start;
            for (int step = 1; step <= steps; step++)
            {
                if (probes >= MaximumEscapeProbes) return false;
                probes++;
                var next = ground(start + heading * (distance * step / steps));
                if (!next.HasValue || !PlayerWorldScope.Finite(next.Value) || Math.Abs(next.Value.y - previous.y) > .75f * distance / steps + .05f || !clear(previous, next.Value)) break;
                previous = next.Value;
                if (step == steps) return true;
            }
        }
        return false;
    }
}
