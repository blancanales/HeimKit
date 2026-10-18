namespace HeimKit.Features.SharedMap.Models;

internal sealed class MapReplicationSnapshot
{
    public SharedMapController Controller { get; }
    public TableMapSnapshot? Table { get; set; }
    public byte[]? Terrain { get; set; }
    public int PreviousPrefab { get; set; }
    public string MiningString { get; set; } = "";
    public float MiningHealth { get; set; } = float.NaN;
    public MapReplicationSnapshot(SharedMapController controller) => Controller = controller;
}
