using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

// This is publication cleanup only. Saved entrances, visits and world objects retain their identities.
public sealed class SharedMapDungeonPins
{
    public const float DuplicateDistanceMeters = 10;
    private readonly Dictionary<(int X, int Z), List<MapMarker>> _anchors = new();
    public IReadOnlyList<MapMarker> Markers { get; }
    public int Suppressed { get; }

    public SharedMapDungeonPins(IReadOnlyList<MapMarker> markers)
    {
        var kept = new List<MapMarker>();
        int suppressed = 0;
        // Stable fixed anchors: A close to B and B close to C must not absorb C through a chain.
        foreach (var marker in markers.OrderBy(p => p.Identity, StringComparer.Ordinal)
            .ThenBy(p => p.X).ThenBy(p => p.Z).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            if (!SharedMapExploration.Finite(marker.X) || !SharedMapExploration.Finite(marker.Y) || !SharedMapExploration.Finite(marker.Z))
                throw new System.IO.InvalidDataException("Invalid dungeon publication point.");
            if (Near(marker.X, marker.Z).Any()) { suppressed++; continue; }
            kept.Add(marker);
            var cell = Cell(marker.X, marker.Z);
            if (!_anchors.TryGetValue(cell, out var group)) _anchors.Add(cell, group = new List<MapMarker>());
            group.Add(marker);
        }
        Markers = kept;
        Suppressed = suppressed;
    }
    private static (int X, int Z) Cell(float x, float z) =>
        ((int)Math.Floor(x / DuplicateDistanceMeters), (int)Math.Floor(z / DuplicateDistanceMeters));
    private IEnumerable<MapMarker> Near(float x, float z)
    {
        var cell = Cell(x, z);
        for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++)
            if (_anchors.TryGetValue((cell.X + dx, cell.Z + dz), out var group))
                foreach (var marker in group)
                {
                    double a = (double)marker.X - x, b = (double)marker.Z - z;
                    if (a * a + b * b <= DuplicateDistanceMeters * DuplicateDistanceMeters) yield return marker;
                }
    }
    public static string? Family(string label)
    {
        string name = label.Trim();
        int split = name.LastIndexOf(' ');
        if (split > 0 && name.EndsWith("%", StringComparison.Ordinal) &&
            int.TryParse(name.Substring(split + 1, name.Length - split - 2), NumberStyles.None, CultureInfo.InvariantCulture, out int percent) &&
            percent >= 0 && percent <= 100) name = name.Substring(0, split);
        return SharedMapDungeonPrefab.Labels.Values.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }
    public bool Obsolete(MapPin pin) => pin.Icon == (int)Minimap.PinType.Icon2 && Family(pin.Name) != null && Near(pin.X, pin.Z).Any();
}
