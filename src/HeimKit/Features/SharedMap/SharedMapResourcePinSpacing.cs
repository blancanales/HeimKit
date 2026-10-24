using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

// Display priority only: discoveries, depletion, grouping and personal pins are untouched.
public static class SharedMapResourcePinSpacing
{
    public static int Apply(List<MapPin> publications, float distance)
    {
        if (!SharedMapExploration.Finite(distance) || distance < 0 || distance > 100)
            throw new ArgumentOutOfRangeException(nameof(distance));
        if (distance == 0) return 0;
        double cellSize = Math.Max(1, distance), squared = (double)distance * distance;
        var cells = new Dictionary<(double X, double Z), List<MapPin>>();
        (double X, double Z) Cell(MapPin pin) => (Math.Floor(pin.X / cellSize), Math.Floor(pin.Z / cellSize));
        foreach (var pin in publications)
        {
            if (pin.Owner == SharedMapTableData.ResourceOwner || !SharedMapTableData.ManagedOwner(pin.Owner)) continue;
            var cell = Cell(pin);
            if (!cells.TryGetValue(cell, out var group)) cells.Add(cell, group = new List<MapPin>());
            group.Add(pin);
        }
        if (cells.Count == 0) return 0;
        bool Hidden(MapPin pin)
        {
            if (pin.Owner != SharedMapTableData.ResourceOwner) return false;
            var cell = Cell(pin);
            for (int x = -1; x <= 1; x++) for (int z = -1; z <= 1; z++)
                if (cells.TryGetValue((cell.X + x, cell.Z + z), out var group))
                    foreach (var other in group)
                    {
                        double dx = (double)pin.X - other.X, dz = (double)pin.Z - other.Z;
                        if (dx * dx + dz * dz <= squared) return true;
                    }
            return false;
        }
        return publications.RemoveAll(Hidden);
    }
}
