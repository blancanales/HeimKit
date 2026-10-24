using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

internal sealed class TameTravelPlacementAccess
{
    private readonly List<Vector3> _path = new();
    private readonly int _walls = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
    internal bool Reachable(Vector3 traveller, Vector3 landing, out string reason)
    {
        reason = "access_path_unready_or_blocked";
        // A player-sized route can pass a doorway even when the animal cannot.
        // Full paths and endpoint checks prevent native extended snapping across a wall.
        if (Pathfinding.instance == null || !Pathfinding.instance.GetPath(traveller, landing, _path,
            Pathfinding.AgentType.HumanoidNoSwim, requireFullPath: true, cleanup: false, havePath: true) || _path.Count == 0) return false;
        return Validate(traveller, landing, _path, Clear, out reason);
    }
    internal static bool Validate(Vector3 traveller, Vector3 landing, IReadOnlyList<Vector3> path,
        Func<Vector3, Vector3, bool> clear, out string reason)
    {
        reason = "access_endpoint_snap";
        if (path.Count == 0 || path.Count > 128 || !Near(path[0], traveller) || !Near(path[path.Count - 1], landing))
        { reason = "access_endpoint_snap"; return false; }
        float length = 0;
        var previous = traveller;
        for (int i = 0; i <= path.Count; i++)
        {
            var next = i < path.Count ? path[i] : landing;
            if (!PlayerWorldScope.Finite(next)) { reason = "access_path_invalid"; return false; }
            length += Vector3.Distance(previous, next);
            if (length > Vector3.Distance(traveller, landing) + 12 || length > 96)
            { reason = "access_route_detour"; return false; }
            var delta = next - previous;
            // Recheck current colliders: a stale navmesh must not authorize crossing a wall.
            if (delta.sqrMagnitude > .0001f && !clear(previous, next))
            { reason = "access_wall_collision"; return false; }
            previous = next;
        }
        reason = "accessible"; return true;
    }
    private bool Clear(Vector3 start, Vector3 end) => !Occupied(start) && !Occupied(end) &&
        !Blocked(start, end - start, .8f) && !Blocked(start, end - start, 1.5f);
    private bool Occupied(Vector3 point) => Physics.CheckSphere(point + Vector3.up * .8f, .2f, _walls, QueryTriggerInteraction.Ignore) ||
        Physics.CheckSphere(point + Vector3.up * 1.5f, .2f, _walls, QueryTriggerInteraction.Ignore);
    private bool Blocked(Vector3 start, Vector3 delta, float height) => Physics.SphereCast(start + Vector3.up * height,
        .2f, delta.normalized, out _, delta.magnitude, _walls, QueryTriggerInteraction.Ignore);
    private static bool Near(Vector3 point, Vector3 target)
    {
        var delta = point - target;
        return new Vector2(delta.x, delta.z).sqrMagnitude <= 1 && Mathf.Abs(delta.y) <= 1;
    }
}
