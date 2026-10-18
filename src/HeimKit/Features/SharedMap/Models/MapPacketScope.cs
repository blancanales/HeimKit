namespace HeimKit.Features.SharedMap.Models;

internal sealed class MapPacketScope
{
    public SharedMapController? Previous { get; }
    public ZNetPeer? PreviousPeer { get; }
    public SharedMapController? Current { get; set; }
    public MapPacketScope(SharedMapController? previous, ZNetPeer? previousPeer)
    { Previous = previous; PreviousPeer = previousPeer; }
}
