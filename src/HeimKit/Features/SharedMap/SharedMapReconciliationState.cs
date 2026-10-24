using System.Collections.Generic;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

// Compare complete published inputs, without a collision-prone aggregate hash.
internal sealed class SharedMapReconciliationState
{
    internal readonly int Pixels;
    internal readonly long Pins, Removals, PortalPublications;
    internal readonly float HideDistance;
    internal readonly IReadOnlyList<MapMarker> Portals, Resources, Dungeons, Runestones, Spawners;
    internal SharedMapReconciliationState(int pixels, long pins, long removals, long portalPublications, float hideDistance,
        IReadOnlyList<MapMarker> portals, IReadOnlyList<MapMarker> resources, IReadOnlyList<MapMarker> dungeons,
        IReadOnlyList<MapMarker> runestones, IReadOnlyList<MapMarker> spawners)
    {
        Pixels = pixels; Pins = pins; Removals = removals; PortalPublications = portalPublications; HideDistance = hideDistance;
        Portals = Copy(portals); Resources = Copy(resources); Dungeons = Copy(dungeons); Runestones = Copy(runestones); Spawners = Copy(spawners);
    }
    private static MapMarker[] Copy(IReadOnlyList<MapMarker> source)
    { var result = new MapMarker[source.Count]; for (int i = 0; i < result.Length; i++) result[i] = source[i]; return result; }
    internal bool Same(SharedMapReconciliationState other) => Pixels == other.Pixels && Pins == other.Pins &&
        Removals == other.Removals && PortalPublications == other.PortalPublications && HideDistance == other.HideDistance &&
        SameMarkers(Portals, other.Portals) && SameMarkers(Resources, other.Resources) && SameMarkers(Dungeons, other.Dungeons) &&
        SameMarkers(Runestones, other.Runestones) && SameMarkers(Spawners, other.Spawners);
    internal static bool SameMarkers(IReadOnlyList<MapMarker> left, IReadOnlyList<MapMarker> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            var a = left[i]; var b = right[i];
            if (a.Name != b.Name || a.Identity != b.Identity || a.X != b.X || a.Y != b.Y || a.Z != b.Z) return false;
        }
        return true;
    }
}
