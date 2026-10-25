using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Features.SharedMap.Models;

public sealed class MapMarker
{
    public string Name { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public string Identity { get; }
    public MapMarker(string name, float x, float y, float z, string identity = "")
    { Name = name; X = x; Y = y; Z = z; Identity = identity; }
}
