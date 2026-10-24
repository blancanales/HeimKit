using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
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
