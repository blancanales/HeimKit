using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

public static class SharedMapResourceClusters
{
    public static IReadOnlyList<MapMarker> Group(IEnumerable<ResourceDeposit> discoveries, float distance, bool removeDepleted)
    {
        var groups = SharedMapMarkerClusters.Group(discoveries.Where(d => d.Kind != ResourceKind.Silver || d.SilverDiscovered).OrderBy(d => d.Identity), distance,
            d => d.X, d => d.Z, (a, b) => a.Kind == b.Kind);
        var markers = new List<MapMarker>();
        foreach (var group in groups)
        {
            var remaining = group.Where(d => !removeDepleted || !d.Depleted).ToArray();
            if (remaining.Length == 0) continue;
            string name = group[0].Kind.ToString();
            if (group[0].Kind == ResourceKind.Copper || group[0].Kind == ResourceKind.Silver) name = PercentageLabel(group);
            if (group[0].Kind == ResourceKind.Tin && remaining.Length > 1)
                name += " (" + remaining.Length.ToString(CultureInfo.InvariantCulture) + ")";
            markers.Add(new MapMarker(name, (float)remaining.Average(d => (double)d.X),
                (float)remaining.Average(d => (double)d.Y), (float)remaining.Average(d => (double)d.Z),
                group[0].Identity.ToString(CultureInfo.InvariantCulture)));
            if (markers.Count > 4096) throw new InvalidOperationException("Community resource marker limit exceeded; table data was not replaced.");
        }
        return markers;
    }

    private static string PercentageLabel(IReadOnlyList<ResourceDeposit> group)
    {
        string label = group[0].Kind.ToString();
        if (group.All(d => d.Depleted)) return label + " 0%";
        if (group.Any(d => !d.Progress.HasValue)) return label + " ?%";
        // Include depleted members' original totals. Otherwise mining a whole boulder
        // could make the remaining group's percentage jump back to 100%.
        long total = group.Sum(d => (long)d.Progress!.Value.TotalSections);
        long remaining = group.Sum(d => d.Depleted ? 0L : d.Progress!.Value.RemainingSections);
        long percent = (remaining * 100 + total - 1) / total;
        return label + " " + percent.ToString(CultureInfo.InvariantCulture) + "%";
    }
}
