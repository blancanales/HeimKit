using System;
using HeimKit.Core.Native;
using HeimKit.Core.World;
using HeimKit.Features.StationSupply.Models;
using UnityEngine;
namespace HeimKit.Features.StationSupply;
internal static class StationSupplyRules
{
    internal static float? Distance(Vector3 station, Quaternion rotation, Vector3 localIntake, Vector3 chest, float range, float height)
        => Distance(station, rotation, localIntake, chest, range, height, out _);
    internal static float? Distance(Vector3 station, Quaternion rotation, Vector3 localIntake, Vector3 chest, float range, float height, out string reason)
        => Distance(Placement(station, rotation, localIntake, range, height), chest, out reason);
    internal static SupplyPlacement Placement(Vector3 station, Quaternion rotation, Vector3 localIntake, float range, float height)
    {
        bool valid = PlayerWorldScope.Finite(station) && PlayerWorldScope.Finite(localIntake) && PlayerWorldScope.Finite(range) && PlayerWorldScope.Finite(height) && range > 0 && height >= 0;
        var anchor = rotation * localIntake; anchor.y = 0;
        return new SupplyPlacement(station, anchor, range, height, valid);
    }
    internal static float? Distance(in SupplyPlacement placement, Vector3 chest, out string reason)
    {
        reason = "invalid_position_or_limits";
        if (!placement.Valid || !PlayerWorldScope.Finite(chest)) return null;
        if (Math.Abs(chest.y - placement.Station.y) > placement.Height) { reason = "chest_height_exceeded"; return null; }
        var offset = chest - placement.Station - placement.Anchor; offset.y = 0;
        // A chest behind the intake towards the station centre belongs elsewhere.
        if (placement.HasSide && Vector3.Dot(offset, placement.Outward) < -0.25f) { reason = "chest_on_inward_side"; return null; }
        float distance = offset.sqrMagnitude;
        if (!PlayerWorldScope.Finite(distance)) return null;
        if (distance > placement.RangeSquared) { reason = "chest_range_exceeded"; return null; }
        reason = "eligible_chest"; return distance;
    }
    internal static bool NativeScale(ZDO record)
    {
        var vector = record.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
        if (vector != Vector3.zero) return PlayerWorldScope.Finite(vector) && (vector - Vector3.one).sqrMagnitude < .0001f;
        float scalar = record.GetFloat(ZDOVars.s_scaleScalarHash, 1f);
        return PlayerWorldScope.Finite(scalar) && Math.Abs(scalar - 1f) < .0001f;
    }
    internal static bool Prefer(float candidate, ZDOID candidateId, float best, ZDOID bestId)
        => candidate < best || candidate == best && NativeIdentity.CompareOrdinal(candidateId, bestId) < 0;
    internal static int VisitRoom(SupplyKind kind, float fuel, int queued, int content, int capacity, int maximum)
    {
        if (kind == SupplyKind.Fermenter) return content == 0 ? Math.Min(maximum, 1) : 0;
        if (kind == SupplyKind.Material) return QueueRoom(queued, capacity) ? Math.Min(maximum, capacity - queued) : 0;
        if (kind == SupplyKind.Obliterator) return maximum;
        int count = 0; while (count < maximum && FuelRoom(fuel, capacity)) { fuel += 1f; count++; } return count;
    }
    internal static bool FuelRoom(float fuel, int capacity) => PlayerWorldScope.Finite(fuel) && fuel >= 0 && capacity > 0 && fuel <= capacity - 1;
    internal static bool QueueRoom(int queued, int capacity) => queued >= 0 && capacity > 0 && queued < capacity;
}
