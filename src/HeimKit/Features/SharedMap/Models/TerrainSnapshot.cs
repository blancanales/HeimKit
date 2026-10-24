using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Features.SharedMap.Models;

public sealed class TerrainSnapshot
{
    public int Operations { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    internal float[] Heights { get; }
    internal TerrainSnapshot(int operations, float x, float y, float z, float[] heights)
    { Operations = operations; X = x; Y = y; Z = z; Heights = heights; }
}
