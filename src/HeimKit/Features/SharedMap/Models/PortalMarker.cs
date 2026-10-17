namespace HeimKit.Features.SharedMap.Models;

public sealed class PortalMarker
{
    public string Name { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public string Identity { get; }
    public PortalMarker(string name, float x, float y, float z, string identity = "")
    { Name = name; X = x; Y = y; Z = z; Identity = identity; }
}
