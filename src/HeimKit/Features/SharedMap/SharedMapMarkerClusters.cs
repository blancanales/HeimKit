using System;
using System.Collections.Generic;

namespace HeimKit.Features.SharedMap;

// Input order selects fixed anchors. A nearby member never extends a group's reach.
public static class SharedMapMarkerClusters
{
    public static IReadOnlyList<List<T>> Group<T>(IEnumerable<T> ordered, float distance,
        Func<T, float> x, Func<T, float> z, Func<T, T, bool> sameKind)
    {
        if (!SharedMapExploration.Finite(distance) || distance < 0 || distance > 100)
            throw new ArgumentOutOfRangeException(nameof(distance));
        var groups = new List<List<T>>();
        var cells = new Dictionary<(long, long), List<int>>();
        foreach (var item in ordered)
        {
            List<T>? selected = null;
            double closest = (double)distance * distance;
            int selectedIndex = int.MaxValue;
            long cx = distance > 0 ? (long)Math.Max(-1e12, Math.Min(1e12, Math.Floor((double)x(item) / Math.Max(distance, 1f)))) : 0;
            long cz = distance > 0 ? (long)Math.Max(-1e12, Math.Min(1e12, Math.Floor((double)z(item) / Math.Max(distance, 1f)))) : 0;
            if (distance > 0)
                for (long a = cx - 1; a <= cx + 1; a++)
                    for (long b = cz - 1; b <= cz + 1; b++)
                        if (cells.TryGetValue((a, b), out var nearbyAnchors))
                            foreach (int index in nearbyAnchors)
                            {
                                var group = groups[index];
                                if (!sameKind(item, group[0])) continue;
                                double dx = (double)x(item) - x(group[0]), dz = (double)z(item) - z(group[0]);
                                double squared = dx * dx + dz * dz;
                                if (squared > closest || squared == closest && index >= selectedIndex) continue;
                                selected = group; closest = squared; selectedIndex = index;
                            }
            if (selected == null)
            {
                if (!cells.TryGetValue((cx, cz), out var anchors)) cells.Add((cx, cz), anchors = new List<int>());
                anchors.Add(groups.Count); groups.Add(new List<T> { item });
            }
            else selected.Add(item);
        }
        return groups;
    }
}
