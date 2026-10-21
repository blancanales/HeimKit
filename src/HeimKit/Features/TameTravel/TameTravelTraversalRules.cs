using System;
using HeimKit.Shared;
using UnityEngine;
namespace HeimKit.Features.TameTravel;
internal static class TameTravelTraversalRules
{
    public static string CancellationMessage(string reason) => reason switch
    {
        "destination_distance_exceeded" or "traveller_left_destination" => "Portal attempt ended: stay within eight metres of the destination exit.",
        "arrival_timeout" or "traversal_timeout" => "Portal attempt ended: the linked arrival did not finish in time.",
        "pair_or_actor_changed" or "portal_pair_changed" => "Portal attempt ended: the portal link or character changed.",
        "stale_position_observation" or "position_history_overflow" => "Portal attempt ended: player position updates were incomplete.",
        "animal_left_source_range" => "Portal attempt ended: the animal moved too far from the source portal.",
        _ => "Portal attempt ended before animal relocation."
    };
    public static string? SourceRejection(float age, float gap, float distanceSquared)
    {
        if (!PlayerWorldScope.Finite(age) || age < 0 || age > 2) return "stale_position_observation";
        if (!PlayerWorldScope.Finite(distanceSquared) || distanceSquared > 144) return "jump_without_trigger_witness";
        if (!PlayerWorldScope.Finite(gap) || gap < 0 || gap > 2) return "source_sample_gap";
        return null;
    }
    public static string? DestinationRejection(float distanceSquared, float age, float window)
    {
        if (!PlayerWorldScope.Finite(distanceSquared) || distanceSquared > 64) return "destination_distance_exceeded";
        return age > window + 30 ? "traversal_timeout" : null;
    }
    public static string? ArrivalRejection(bool atExit, float age, float sourceDistanceSquared)
    {
        // Replication may expose the source trigger late in the native wind-up.
        // A matching endpoint before our minimum age waits, rather than being
        // mistaken for movement outside both linked endpoints.
        if (atExit) return age < 2 ? "arrival_too_early" : null;
        return sourceDistanceSquared > 144 ? "movement_outside_linked_endpoints" : null;
    }
    // Managed segment/AABB test, including an observation already inside. Native
    // teleport activation need not align with our former polling boundary.
    public static bool Touches(Bounds box, Vector3 before, Vector3 after)
    {
        if (!PlayerWorldScope.Finite(before) || !PlayerWorldScope.Finite(after)) return false;
        float near = 0, far = 1;
        var min = box.min; var max = box.max;
        bool beforeInside = true, afterInside = true;
        for (int axis = 0; axis < 3; axis++)
        {
            beforeInside &= before[axis] >= min[axis] && before[axis] <= max[axis];
            afterInside &= after[axis] >= min[axis] && after[axis] <= max[axis];
        }
        if (afterInside) return true;
        if (beforeInside) return false; // Leaving alone is not a new source witness.
        for (int axis = 0; axis < 3; axis++)
        {
            float delta = after[axis] - before[axis];
            if (Math.Abs(delta) < .00001f) { if (before[axis] < min[axis] || before[axis] > max[axis]) return false; continue; }
            float a = (min[axis] - before[axis]) / delta, b = (max[axis] - before[axis]) / delta;
            near = Math.Max(near, Math.Min(a, b)); far = Math.Min(far, Math.Max(a, b));
            if (near > far) return false;
        }
        return true;
    }
}
