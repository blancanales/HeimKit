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

namespace HeimKit.Features.ForestRenewal;

// Saved prefab geometry avoids requiring ownership or live instances of player structures.
internal sealed class ForestRenewalStructures
{
    private readonly PrefabCatalog<(bool Piece, Bounds? Bounds, Vector3 Scale)> _definitions = PrefabRegistry.Current.Catalog<(bool Piece, Bounds? Bounds, Vector3 Scale)>("ForestRenewalStructures._definitions");
    public bool TryExcluded(IReadOnlyList<ZDO> records, Vector3 point, float clearance, out bool excluded, out ZDO? blocker, out string? reason)
    {
        excluded = false; blocker = null; reason = null;
        if (clearance <= 0) return true;
        for (int index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (!record.IsValid() || record.GetLong(ZDOVars.s_creator) == 0) continue;
            if (!_definitions.TryGetValue(record.GetPrefab(), out var definition))
            {
                var prefab = PrefabRegistry.Current.Prefab(record.GetPrefab());
                if (prefab == null) { blocker = record; reason = "prefab_unavailable"; return false; }
                bool piece = PrefabRegistry.Component<Piece>(prefab) != null;
                definition = (piece, piece ? PrefabRegistry.Current.Bounds(prefab) : null, prefab.transform.localScale);
                if (_definitions.Count < 4096) _definitions.Add(record.GetPrefab(), definition);
            }
            if (!definition.Piece) continue;
            var local = definition.Bounds;
            if (!local.HasValue || !PlayerWorldScope.Finite(local.Value.center) || !PlayerWorldScope.Finite(local.Value.extents))
            { blocker = record; reason = "collision_geometry_unsupported"; return false; }
            var scale = record.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
            if (scale == Vector3.zero)
            {
                float scalar = record.GetFloat(ZDOVars.s_scaleScalarHash, float.NaN);
                scale = float.IsNaN(scalar) ? definition.Scale : Vector3.one * scalar;
            }
            if (!PlayerWorldScope.Finite(scale) || scale.x <= 0 || scale.y <= 0 || scale.z <= 0 || scale.x > 16 || scale.y > 16 || scale.z > 16)
            { blocker = record; reason = "structure_scale_unsupported"; return false; }
            var position = record.GetPosition(); var rotation = record.GetRotation();
            if (!PlayerWorldScope.Finite(position) || !PlayerWorldScope.Finite(rotation.x) || !PlayerWorldScope.Finite(rotation.y) || !PlayerWorldScope.Finite(rotation.z) || !PlayerWorldScope.Finite(rotation.w))
            { blocker = record; reason = "structure_transform_invalid"; return false; }
            if (!CanReach(local.Value, scale, rotation, position, point, clearance)) continue;
            var world = new Bounds(position + rotation * Vector3.Scale(local.Value.center, scale), Vector3.zero);
            for (int i = 0; i < 8; i++) world.Encapsulate(position + rotation * Vector3.Scale(PrefabCollisionBounds.Corner(local.Value, i), scale));
            // Horizontal clearance also protects structures above/below the proposed tree.
            float x = Math.Max(world.min.x - point.x, Math.Max(0, point.x - world.max.x));
            float z = Math.Max(world.min.z - point.z, Math.Max(0, point.z - world.max.z));
            if (x * x + z * z <= clearance * clearance) { excluded = true; return true; }
        }
        return true;
    }
    internal static bool CanReach(Bounds local, Vector3 scale, Quaternion rotation, Vector3 position, Vector3 point, float clearance)
    {
        // Bound every transformed corner by its distance from the pivot, then
        // enclose the horizontal AABB too, preserving its conservative corners.
        // The quaternion factor covers non-unit replicated rotations.
        float norm = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
        float radius = (local.center.magnitude + local.extents.magnitude) * Math.Max(scale.x, Math.Max(scale.y, scale.z)) * Math.Max(1, 2 * norm - 1) * 1.4142136f;
        float x = position.x - point.x, z = position.z - point.z;
        return x * x + z * z <= (clearance + radius) * (clearance + radius);
    }
}
