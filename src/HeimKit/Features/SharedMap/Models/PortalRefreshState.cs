namespace HeimKit.Features.SharedMap.Models;

internal sealed class PortalRefreshState
{
    public string Name { get; }
    public int Slot { get; }
    public float X { get; }
    public float Z { get; }
    public PortalRefreshState(string name, int slot, float x, float z) { Name = name; Slot = slot; X = x; Z = z; }
}
