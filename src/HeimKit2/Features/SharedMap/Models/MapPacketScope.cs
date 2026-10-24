using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Features.SharedMap.Models;

internal sealed class MapPacketScope
{
    public SharedMapController? Previous { get; }
    public ZNetPeer? PreviousPeer { get; }
    public SharedMapController? Current { get; set; }
    public MapPacketScope(SharedMapController? previous, ZNetPeer? previousPeer)
    { Previous = previous; PreviousPeer = previousPeer; }
}
