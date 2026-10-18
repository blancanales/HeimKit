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
