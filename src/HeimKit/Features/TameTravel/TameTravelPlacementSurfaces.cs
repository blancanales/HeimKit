using System;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

// Probe actual low courtyard paving instead of assuming terrain is the walking surface.
internal sealed class TameTravelPlacementSurfaces
{
    private readonly RaycastHit[] _hits = new RaycastHit[32];
    private readonly int _pieces = LayerMask.GetMask("piece");
    internal bool Probe(Vector3 point, out float height, out Vector3 normal, out string reason)
    {
        normal = Vector3.up; reason = "terrain_missing";
        if (!Heightmap.GetHeight(point, out height)) return false;
        var ground = new Vector3(point.x, height, point.z);
        ZoneSystem.instance.GetGroundData(ref ground, out normal, out _, out _, out var map);
        if (map == null) return false;
        if (map.GetLava(ground) > 0) { reason = "lava"; return false; }
        float terrain = height;
        int count = Physics.RaycastNonAlloc(new Vector3(point.x, terrain + 3.5f, point.z), Vector3.down,
            _hits, 3.6f, _pieces, QueryTriggerInteraction.Ignore);
        if (count == _hits.Length) { reason = "surface_probe_capacity"; return false; }
        for (int i = 0; i < count; i++)
        {
            var hit = _hits[i];
            if (hit.point.y <= height || hit.normal.y < .8f) continue;
            var piece = hit.collider.GetComponentInParent<Piece>();
            var structure = piece == null ? null : piece.GetComponent<WearNTear>();
            // Furniture, vehicles and steep/raised construction are not landing supports.
            if (piece == null || !piece.IsPlacedByPlayer() || piece.m_comfort != 0 || structure == null ||
                !structure.m_staticPosition || !structure.m_supports ||
                !LowSlab(hit.collider.bounds.size, hit.point.y - terrain)) continue;
            height = hit.point.y; normal = hit.normal;
        }
        reason = normal.y < .8f ? "slope" : height <= ZoneSystem.instance.m_waterLevel + .5f ? "water" : "supported";
        return reason == "supported";
    }
    internal static bool LowSlab(Vector3 size, float aboveTerrain) =>
        aboveTerrain >= 0 && aboveTerrain <= 3 && size.y > 0 && size.y <= .75f && size.x >= .5f && size.z >= .5f;
}
