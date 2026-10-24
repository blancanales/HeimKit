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

// A native door also owns WearNTear: retain its surrounding native support/roof context.
internal sealed class DoorKeeperContext : IDisposable
{
    private readonly TerrainContext _terrain = new();
    private readonly PrefabCatalog<(bool Required, Bounds? Bounds)> _definitions = PrefabRegistry.Current.Catalog<(bool Required, Bounds? Bounds)>("DoorKeeperContext._definitions");
    private readonly PrefabCatalog<Bounds?> _doorBounds = PrefabRegistry.Current.Catalog<Bounds?>("DoorKeeperContext._doorBounds");
    public string? WaitReason => _terrain.WaitReason;
    public ZDOID WaitObject => _terrain.WaitObject;
    public bool Prepare(PlayerWorldScope scope, ZDO door, float now)
    {
        var point = door.GetPosition();
        int doorHash = door.GetPrefab();
        var doorPrefab = PrefabRegistry.Current.Prefab(doorHash);
        if (doorPrefab == null) return false;
        if (!_doorBounds.TryGetValue(doorHash, out var doorBounds))
            _doorBounds[doorHash] = doorBounds = PrefabRegistry.Current.Bounds(doorPrefab);
        if (!doorBounds.HasValue) return false;
        var doorScale = door.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
        if (doorScale == Vector3.zero)
        {
            float scalar = door.GetFloat(ZDOVars.s_scaleScalarHash, float.NaN);
            doorScale = float.IsNaN(scalar) ? doorPrefab.transform.localScale : Vector3.one * scalar;
        }
        float radius = 16 + Vector3.Scale(doorBounds.Value.center, doorScale).magnitude + Vector3.Scale(doorBounds.Value.extents, doorScale).magnitude;
        if (!PlayerWorldScope.Finite(radius)) return false;
        if (!scope.ContextReady() || !_terrain.Prepare(point, scope.Context, now)) return false;
        bool ready = true;
        foreach (var record in scope.Context)
        {
            if (!record.IsValid() || Character.InInterior(record.GetPosition())) continue;
            var prefab = PrefabRegistry.Current.Prefab(record.GetPrefab());
            if (prefab == null) { ready = false; continue; }
            if (!_definitions.TryGetValue(record.GetPrefab(), out var definition))
            {
                bool required = PrefabRegistry.Component<Character>(prefab) == null && PrefabRegistry.Component<ItemDrop>(prefab) == null &&
                    (PrefabRegistry.Component<Piece>(prefab) != null || PrefabRegistry.Children<Collider>(prefab, true).Length > 0 ||
                        PrefabRegistry.Component<TerrainComp>(prefab) != null || PrefabRegistry.Component<TerrainModifier>(prefab) != null);
                definition = (required, required ? PrefabRegistry.Current.Bounds(prefab) : null);
                if (_definitions.Count < 4096) _definitions[record.GetPrefab()] = definition;
            }
            if (!definition.Required) continue;
            // Keep a conservative horizontal neighbourhood, including tall roof
            // context and large/offset colliders whose pivots are farther away.
            // Unknown geometry remains included rather than guessed absent.
            if (definition.Bounds.HasValue)
            {
                var scale = record.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
                if (scale == Vector3.zero)
                {
                    float scalar = record.GetFloat(ZDOVars.s_scaleScalarHash, float.NaN);
                    scale = float.IsNaN(scalar) ? prefab.transform.localScale : Vector3.one * scalar;
                }
                if (!PlayerWorldScope.Finite(scale)) return false;
                var bounds = definition.Bounds.Value;
                float reach = Vector3.Scale(bounds.center, scale).magnitude + Vector3.Scale(bounds.extents, scale).magnitude;
                var delta = record.GetPosition() - point;
                if (delta.x * delta.x + delta.z * delta.z > (radius + reach) * (radius + reach)) continue;
            }
            if (!_terrain.Include(record, now)) return false;
            if (ZNetScene.instance.FindInstance(record) == null) ready = false;
        }
        return ready;
    }
    public void Clear() => _terrain.Clear();
    public void Dispose() => _terrain.Dispose();
}
