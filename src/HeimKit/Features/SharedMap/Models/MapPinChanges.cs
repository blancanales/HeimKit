using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System.Collections.Generic;

namespace HeimKit.Features.SharedMap.Models;

public sealed class MapPinChanges
{
    public List<MapPin> Added { get; } = new();
    public List<MapPin> Removed { get; } = new();
    public List<(MapPin Before, MapPin After)> Updated { get; } = new();
    public bool Any => Added.Count != 0 || Removed.Count != 0 || Updated.Count != 0;
}
