using System.Collections.Generic;

namespace HeimKit.Features.SharedMap.Models;

public sealed class MapPinChanges
{
    public List<MapPin> Added { get; } = new();
    public List<MapPin> Removed { get; } = new();
    public List<(MapPin Before, MapPin After)> Updated { get; } = new();
    public bool Any => Added.Count != 0 || Removed.Count != 0 || Updated.Count != 0;
}
