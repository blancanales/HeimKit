using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.Linq;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

public static class SharedMapPortalClusters
{
    public static IReadOnlyList<MapMarker> Group(IReadOnlyList<MapMarker> portals, float distance)
    {
        if (!SharedMapExploration.Finite(distance) || distance < 0 || distance > 100)
            throw new ArgumentOutOfRangeException(nameof(distance));
        if (portals.Count > 4096) throw new ArgumentOutOfRangeException(nameof(portals));
        if (distance == 0) return portals;
        var groups = SharedMapMarkerClusters.Group(portals, distance, p => p.X, p => p.Z, (a, b) => true);
        return groups.Select(group => group.Count == 1 ? group[0] :
            new MapMarker("Multi Portals (" + group.Count + ")", (float)group.Average(p => (double)p.X),
                (float)group.Average(p => (double)p.Y), (float)group.Average(p => (double)p.Z), group[0].Identity)).ToArray();
    }
}
