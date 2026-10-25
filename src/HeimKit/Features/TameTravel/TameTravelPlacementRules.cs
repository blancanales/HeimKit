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
    internal const int MaximumPushes = 3;
    internal const float LandingRadius = 48, ContextRadius = 56;
    // Sideways room kept between the animal's body and buildings or objects.
    internal const float ObstaclePadding = .75f;
    // Every animal's arcs move out in the same small steps, up to the sweep limit around the traveller.
    internal const float ArcStep = .25f, SweepLimit = 12;
    internal static float GridSpacing(float extent) => Math.Max(2, Math.Min(3, extent));
    // The first arc sits just outside the traveller's body clearance.
    internal static float ArcStart(float extent) => extent + 1.55f;
    // Neighbouring spots on an arc are a quarter of the animal's width apart, never closer than one arc step.
    internal static float ArcSpacing(float extent) => Math.Max(ArcStep, extent * .5f);
    internal static int Arcs(float extent) => Math.Max(1, (int)Math.Floor((SweepLimit - ArcStart(extent)) / ArcStep + 1e-4) + 1);
    internal static int CandidateCount(float extent)
    {
        int width = 2 * (int)Math.Ceiling(LandingRadius / GridSpacing(extent)) + 1;
        return ArcCandidates(extent) + width * width * 2;
    }
    internal static int ArcCandidates(float extent)
    {
        int total = 0;
        for (int arc = 0, arcs = Arcs(extent); arc < arcs; arc++) total += ArcPoints(arc, arcs, extent);
        return total;
    }
    // Each arc covers the half beside and ahead of the traveller, widening evenly behind
    // them until the last arc is a full circle.
    private static void Divisions(int arc, int arcs, float extent, out float radius, out int front, out int behind, out float extra, out bool full)
    {
        radius = ArcStart(extent) + arc * ArcStep;
        float spacing = ArcSpacing(extent);
        extra = arcs > 1 ? (float)Math.PI / 2 * arc / (arcs - 1) : 0;
        front = Math.Max(1, (int)Math.Ceiling(Math.PI * radius / spacing));
        behind = extra <= 0 ? 0 : (int)Math.Ceiling(extra * radius / spacing);
        full = arc == arcs - 1 && arcs > 1;
    }
    private static int ArcPoints(int arc, int arcs, float extent)
    {
        Divisions(arc, arcs, extent, out _, out int front, out int behind, out _, out bool full);
        return front + 1 + 2 * behind - (full ? 1 : 0);
    }
    // Windshield-wiper order around the traveller, facing the portal's direction: right side,
    // across the front, left side, then the newly added ground behind the right and the left.
    // The same surroundings always give the same order. Broad grids follow the last arc.
    internal static Vector3 Candidate(int index, float extent, Vector3 exit, Vector3 traveller, Vector3 forward)
    {
        var right = new Vector3(forward.z, 0, -forward.x);
        for (int arc = 0, arcs = Arcs(extent); arc < arcs; arc++)
        {
            Divisions(arc, arcs, extent, out float radius, out int front, out int behind, out float extra, out _);
            int points = ArcPoints(arc, arcs, extent);
            if (index >= points) { index -= points; continue; }
            double angle = index <= front ? Math.PI / 2 - index * Math.PI / front :
                index <= front + behind ? Math.PI / 2 + (index - front) * extra / behind :
                -Math.PI / 2 - (index - front - behind) * extra / behind;
            return traveller + (forward * (float)Math.Cos(angle) + right * (float)Math.Sin(angle)) * radius;
        }
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
    internal static bool SupportedFootprint(float centerHeight, float sampleHeight, Vector3 offset, Vector3 normal)
    {
        if (normal.y < .8f || !PlayerWorldScope.Finite(sampleHeight)) return false;
        float expected = centerHeight - (normal.x * offset.x + normal.z * offset.z) / normal.y;
        return Math.Abs(sampleHeight - expected) <= .5f;
    }
    // The shared step budget ran out before this candidate's walk-out check finished. A check that
    // started with the whole budget and still ran out can never finish, so it counts as blocked.
    internal static bool EscapeDeferred(int probesBefore, int probesAfter) =>
        probesBefore > 0 && probesAfter >= MaximumEscapeProbes;
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
