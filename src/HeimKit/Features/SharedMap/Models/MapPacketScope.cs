namespace HeimKit.Features.SharedMap.Models;

internal sealed class MapPacketScope
{
    public SharedMapController? Previous { get; }
    public SharedMapController? Current { get; set; }
    public MapPacketScope(SharedMapController? previous) => Previous = previous;
}
