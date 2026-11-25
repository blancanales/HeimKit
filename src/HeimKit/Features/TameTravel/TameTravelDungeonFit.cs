using System.Collections.Generic;
using HeimKit.Core.Prefabs;
using HeimKit.Core.World;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

internal sealed class TameTravelDungeonFit
{
    internal static Quaternion Facing(Quaternion rotation) =>
        Quaternion.LookRotation(Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up).normalized);

    private static readonly int Structure = LayerMask.GetMask("terrain", "Default", "Default_small", "piece", "static_solid", "vehicle");
    private static readonly int Bodies = Structure | LayerMask.GetMask("character", "character_net", "character_ghost");
    private readonly PrefabCatalog<Bounds> _bodies = PrefabRegistry.Current.Catalog<Bounds>("TameTravelDungeonBody");

    internal static Bounds SkeletonBounds(ZDO record, Vector3 root)
    {
        var capsule = PrefabRegistry.Component<CapsuleCollider>(PrefabRegistry.Current.Prefab(record.GetPrefab()))!;
        TameTravelRules.Scale(record, out var scale);
        return new Bounds(root + Vector3.Scale(capsule.center, scale),
            Vector3.Scale(new Vector3(2 * capsule.radius, capsule.height, 2 * capsule.radius), scale));
    }

    internal bool Body(ZDO record, out Bounds body)
    {
        body = default;
        if (!TameTravelRules.Scale(record, out var scale)) return false;
        int hash = record.GetPrefab();
        if (!_bodies.TryGetValue(hash, out var local))
        {
            var prefab = PrefabRegistry.Current.Prefab(hash);
            var capsule = PrefabRegistry.Component<CapsuleCollider>(prefab);
            if (prefab == null || capsule == null) return false;
            var size = Vector3.one * (2 * capsule.radius);
            size[capsule.direction] = Mathf.Max(size[capsule.direction], capsule.height);
            local = new Bounds(capsule.center, size);
            // The root capsule can be much smaller than the animal, particularly for a Lox.
            foreach (var skin in PrefabRegistry.Children<SkinnedMeshRenderer>(prefab, true))
            {
                if (skin.sharedMesh == null) continue;
                var toRoot = prefab.transform.worldToLocalMatrix * skin.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                    local.Encapsulate(toRoot.MultiplyPoint3x4(PrefabCollisionBounds.Corner(skin.localBounds, i)));
            }
            _bodies[hash] = local;
        }
        body = new Bounds(Vector3.Scale(local.center, scale), Vector3.Scale(local.size, scale));
        return PlayerWorldScope.Finite(body.center) && PlayerWorldScope.Finite(body.size) &&
            body.size.x > 0 && body.size.y > 0 && body.size.z > 0;
    }

    internal bool Landing(ZDO record, Teleport door, List<Bounds> arrivals, out Vector3 position, out Bounds occupied)
    {
        position = default; occupied = default;
        if (!Body(record, out var body) || door.m_targetPoint == null) return false;
        var half = body.extents + new Vector3(.08f, 0, .08f);
        var exit = door.m_targetPoint.transform;
        var player = PrefabRegistry.Component<CapsuleCollider>(PrefabRegistry.Current.Prefab("Player"));
        if (player == null) return false;
        bool oversized = TameTravelTraversalRules.NeedsFullDoorClearance(body.size, player.radius, player.height);
        if (!Approach(door.transform, half, oversized, out _, out _) ||
            !Approach(exit, half, oversized, out var threshold, out var facing)) return false;
        var forward = facing * Vector3.forward;
        var right = facing * Vector3.right;
        // Try in front of the exit, then to either side, always connected to its mouth.
        for (int row = 0; row < 2; row++)
        for (int lane = -1; lane <= 1; lane++)
        {
            var offset = forward * (1 + half.z + row * (2 * half.z + .4f)) + right * lane * (2 * half.x + .4f);
            if (!Sweep(threshold, offset, facing, half, out var centre) ||
                Physics.CheckBox(centre, half, facing, Bodies, QueryTriggerInteraction.Ignore)) continue;
            var width = facing * Vector3.right * half.x;
            var depth = forward * half.z;
            var size = new Vector3(Mathf.Abs(width.x) + Mathf.Abs(depth.x), half.y, Mathf.Abs(width.z) + Mathf.Abs(depth.z)) * 2;
            var bounds = new Bounds(centre, size);
            if (arrivals.Exists(bounds.Intersects)) continue;
            position = centre - facing * body.center;
            occupied = bounds;
            return true;
        }
        return false;
    }

    private static bool Approach(Transform door, Vector3 half, bool oversized, out Vector3 threshold, out Quaternion facing)
    {
        var forward = Vector3.ProjectOnPlane(door.forward, Vector3.up).normalized;
        threshold = default; facing = Quaternion.identity;
        if (forward.sqrMagnitude < .99f) return false;
        facing = Quaternion.LookRotation(forward);
        // The player just demonstrated a person-sized passage. Larger bodies must also
        // clear the doorway plane itself, even when the room beyond it is empty.
        var mouth = door.position + forward * (oversized ? 0 : half.z + .05f) - Vector3.up;
        if (!Floor(mouth, out var floor)) return false;
        threshold = floor + Vector3.up * (half.y + .12f);
        return !Physics.CheckBox(threshold, half, facing, Structure, QueryTriggerInteraction.Ignore) &&
            Sweep(threshold, forward, facing, half, out _);
    }

    private static bool Sweep(Vector3 start, Vector3 offset, Quaternion facing, Vector3 half, out Vector3 centre)
    {
        centre = start;
        // Stay inside the radius for which FloorWait has retained collision geometry.
        if (half.magnitude + half.z + offset.magnitude + 1 >= 12) return false;
        int steps = Mathf.Max(1, Mathf.CeilToInt(offset.magnitude / .6f));
        for (int i = 1; i <= steps; i++)
        {
            var next = start + offset * ((float)i / steps);
            next.y = centre.y - half.y - .12f;
            if (!Floor(next, out var floor)) return false;
            next = floor + Vector3.up * (half.y + .12f);
            var delta = next - centre;
            if (Mathf.Abs(delta.y) > .4f ||
                Physics.BoxCast(centre, half, delta.normalized, out _, facing, delta.magnitude, Structure, QueryTriggerInteraction.Ignore) ||
                Physics.CheckBox(next, half, facing, Structure, QueryTriggerInteraction.Ignore)) return false;
            // Support the footprint at its corners as well as its centre.
            for (int x = -1; x <= 1; x += 2)
            for (int z = -1; z <= 1; z += 2)
                if (!Floor(floor + facing * new Vector3(x * half.x * .85f, 0, z * half.z * .85f), out var corner) ||
                    Mathf.Abs(corner.y - floor.y) > .4f) return false;
            centre = next;
        }
        return true;
    }

    private static bool Floor(Vector3 point, out Vector3 floor)
    {
        floor = default;
        if (!Physics.Raycast(point + Vector3.up * 1.25f, Vector3.down, out var hit, 2.75f, Structure, QueryTriggerInteraction.Ignore) ||
            hit.rigidbody != null || hit.normal.y < .8f || hit.point.y <= ZoneSystem.instance.m_waterLevel) return false;
        floor = hit.point;
        return true;
    }
}
