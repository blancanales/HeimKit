using System;
using System.Collections.Generic;
using System.Linq;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

public static class SharedMapPortalClusters
{
    public static IReadOnlyList<PortalMarker> Group(IReadOnlyList<PortalMarker> portals, float distance)
    {
        if (!SharedMapExploration.Finite(distance) || distance < 0 || distance > 100)
            throw new ArgumentOutOfRangeException(nameof(distance));
        if (portals.Count > 4096) throw new ArgumentOutOfRangeException(nameof(portals));
        if (distance == 0) return portals;
        var groups = new List<List<PortalMarker>>();
        // A stable native-ID input order selects stable anchors. Compare to the anchor, not a moving
        // centroid or a neighbouring member, so chains cannot join arbitrarily distant portals.
        foreach (var portal in portals)
        {
            List<PortalMarker>? selected = null;
            double closest = (double)distance * distance;
            foreach (var group in groups)
            {
                double dx = (double)portal.X - group[0].X, dz = (double)portal.Z - group[0].Z;
                double squared = dx * dx + dz * dz;
                if (squared > closest) continue;
                if (selected != null && squared == closest) continue;
                selected = group; closest = squared;
            }
            if (selected == null) groups.Add(new List<PortalMarker> { portal });
            else selected.Add(portal);
        }
        return groups.Select(group => group.Count == 1 ? group[0] :
            new PortalMarker("Multi Portals (" + group.Count + ")", (float)group.Average(p => (double)p.X),
                (float)group.Average(p => (double)p.Y), (float)group.Average(p => (double)p.Z), group[0].Identity)).ToArray();
    }
}
