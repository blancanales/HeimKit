using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Features.SharedMap.Models;

internal sealed class MarkerPublicationState
{
    public string Name { get; }
    public int Slot { get; }
    public float X { get; }
    public float Z { get; }
    public MarkerPublicationState(string name, int slot, float x, float z) { Name = name; Slot = slot; X = x; Z = z; }
}
