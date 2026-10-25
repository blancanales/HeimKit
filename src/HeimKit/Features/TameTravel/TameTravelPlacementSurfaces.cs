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

// Probe actual low courtyard paving instead of assuming terrain is the walking surface.
internal sealed class TameTravelPlacementSurfaces
{
    internal const float MaximumSlabThickness = 1.25f;
    private readonly RaycastHit[] _hits = new RaycastHit[32];
    private readonly int _pieces = LayerMask.GetMask("piece");
    // The piece that made the last probe fail with unsupported_surface, for placement evidence.
    public string? Unsupported { get; private set; }
    internal bool Probe(Vector3 point, out float height, out Vector3 normal, out string reason)
    {
        normal = Vector3.up; reason = "terrain_missing"; Unsupported = null;
        if (!Heightmap.GetHeight(point, out height)) return false;
        var ground = new Vector3(point.x, height, point.z);
        ZoneSystem.instance.GetGroundData(ref ground, out normal, out _, out _, out var map);
        if (map == null) return false;
        if (map.GetLava(ground) > 0) { reason = "lava"; return false; }
        float terrain = height;
        int count = Physics.RaycastNonAlloc(new Vector3(point.x, terrain + 3.5f, point.z), Vector3.down,
            _hits, 3.6f, _pieces, QueryTriggerInteraction.Ignore);
        if (count == _hits.Length) { reason = "surface_probe_capacity"; return false; }
        // The highest upward-facing piece above the terrain is what the animal would stand on.
        int top = -1;
        for (int i = 0; i < count; i++)
            if (_hits[i].point.y > terrain && _hits[i].normal.y >= .8f && (top < 0 || _hits[i].point.y > _hits[top].point.y)) top = i;
        if (top >= 0)
        {
            var hit = _hits[top];
            var piece = hit.collider.GetComponentInParent<Piece>();
            var structure = piece == null ? null : piece.GetComponent<WearNTear>();
            // Furniture, vehicles and steep/raised construction are not landing supports. Placing the animal at
            // terrain height beneath such a piece would bury it, so the spot is rejected with the piece named.
            string? rejected = SurfaceRejection(piece != null, piece != null ? piece.m_comfort : 0,
                structure != null, structure != null && structure.m_staticPosition, structure != null && structure.m_supports,
                hit.collider.bounds.size, hit.point.y - terrain);
            for (int i = 0; i < count; i++) _hits[i] = default;
            if (rejected != null)
            {
                Unsupported = (piece != null ? Utils.GetPrefabName(piece.gameObject) : hit.collider.name) + ":" + rejected;
                reason = "unsupported_surface"; return false;
            }
            height = hit.point.y; normal = hit.normal;
        }
        reason = normal.y < .8f ? "slope" : height <= ZoneSystem.instance.m_waterLevel + .5f ? "water" : "supported";
        return reason == "supported";
    }
    internal static bool LowSlab(Vector3 size, float aboveTerrain) => SlabRejection(size, aboveTerrain) == null;
    internal static string? SurfaceRejection(bool piece, int comfort, bool structural, bool isStatic, bool supports, Vector3 size, float aboveTerrain)
        => !piece ? "not_a_piece" : comfort != 0 ? "furniture" : !structural ? "not_structural" :
            !isStatic ? "not_static" : !supports ? "not_supporting" : SlabRejection(size, aboveTerrain);
    // Floors and paving up to stone/marble floor thickness qualify; walls are taller and narrow tops fail the footprint check.
    internal static string? SlabRejection(Vector3 size, float aboveTerrain) =>
        aboveTerrain < 0 || aboveTerrain > 3 ? "raised_construction" :
        size.y <= 0 || size.y > MaximumSlabThickness ? "too_thick" :
        size.x < .5f || size.z < .5f ? "too_narrow" : null;
}
