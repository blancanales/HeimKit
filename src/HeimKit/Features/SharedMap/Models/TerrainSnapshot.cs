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
