namespace HeimKit.Features.SharedMap.Models;

internal sealed class TableMapSnapshot
{
    public SharedMapController Controller { get; }
    public byte[]? Previous { get; }
    public int RemovedDeaths { get; set; }
    public byte[]? Expected { get; set; }
    public TableMapSnapshot(SharedMapController controller, byte[]? previous)
    { Controller = controller; Previous = previous; }
}
