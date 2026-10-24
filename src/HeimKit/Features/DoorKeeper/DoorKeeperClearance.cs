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
namespace HeimKit.Features.DoorKeeper;
internal sealed class DoorKeeperClearance
{
    private readonly PrefabCatalog<Bounds?> _bounds = PrefabRegistry.Current.Catalog<Bounds?>("DoorKeeperClearance._bounds");
    public bool Occupied(PlayerWorldScope scope, ZDO door, float configuredMinimum)
    {
        var prefab = PrefabRegistry.Current.Prefab(door.GetPrefab());
        if (!prefab) return true;
        if (!_bounds.TryGetValue(door.GetPrefab(), out var bounds))
        { bounds = PrefabRegistry.Current.Bounds(prefab); if (_bounds.Count < 4096) _bounds[door.GetPrefab()] = bounds; }
        if (!bounds.HasValue) return true;
        var scale = door.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
        if (scale == Vector3.zero)
        {
            float scalar = door.GetFloat(ZDOVars.s_scaleScalarHash, float.NaN);
            scale = float.IsNaN(scalar) ? prefab.transform.localScale : Vector3.one * scalar;
        }
        if (!PlayerWorldScope.Finite(scale) || scale.x <= 0 || scale.y <= 0 || scale.z <= 0 || Math.Max(scale.x, Math.Max(scale.y, scale.z)) > 16) return true;
        Vector3 center = door.GetPosition() + door.GetRotation() * Vector3.Scale(bounds.Value.center, scale);
        // A full 3D bounding sphere plus player-body allowance covers wide and tall gates.
        float radius = Math.Max(configuredMinimum, Vector3.Scale(bounds.Value.extents, scale).magnitude + 1);
        if (!PlayerWorldScope.Finite(center) || !PlayerWorldScope.Finite(radius)) return true;
        return scope.PlayerNear(center, radius);
    }
}
