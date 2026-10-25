using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Features.SharedMap.Models;

internal sealed class TableMapSnapshot
{
    public SharedMapController Controller { get; }
    public byte[]? Previous { get; }
    public byte[]? Received { get; set; }
    public long? SenderPeer { get; set; }
    public string SenderName { get; set; } = "";
    public int RemovedDeaths { get; set; }
    public byte[]? Expected { get; set; }
    public TableMapSnapshot(SharedMapController controller, byte[]? previous)
    { Controller = controller; Previous = previous; }
}
